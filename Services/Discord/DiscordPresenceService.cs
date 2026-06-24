using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayPresence.Models;
using Playnite.SDK;

namespace PlayPresence.Services.Discord
{
    /// <summary>
    /// Discord Rich Presence over the local Discord IPC pipe, implemented directly with no external
    /// RPC library. This is deliberate: shipping a shared <c>DiscordRPC.dll</c> conflicts with other
    /// Playnite extensions that ship their own (Playnite loads only one version of an assembly), which
    /// caused a TypeLoadException. Talking to the pipe ourselves removes that shared dependency
    /// entirely, so PlayPresence works regardless of what other extensions the user has installed.
    ///
    /// Protocol (see Discord RPC "hard mode" docs): each message is a single buffer of
    /// [opcode:int32 LE][length:int32 LE][utf8 json]. Connect to \\.\pipe\discord-ipc-0..9, send a
    /// HANDSHAKE (op 0) with the application id, await a READY FRAME (op 1), then push SET_ACTIVITY
    /// frames. PINGs (op 3) are answered with PONGs (op 4). The worker thread reconnects forever.
    /// </summary>
    public sealed class DiscordPresenceService : IDiscordPresenceService, IDisposable
    {
        private const int MaxFieldBytes = 128;
        private const int MaxAssetKeyLength = 256;
        private const int MaxButtonLabelBytes = 32;

        private const int OpHandshake = 0;
        private const int OpFrame = 1;
        private const int OpClose = 2;
        private const int OpPing = 3;
        private const int OpPong = 4;

        private static readonly ILogger Logger = LogManager.GetLogger();

        /// <summary>
        /// When false (default), routine connection diagnostics are suppressed so the log stays clean.
        /// Set to true by the plugin only when the user enables debug logging. Warnings and errors are
        /// always logged regardless of this flag.
        /// </summary>
        public static volatile bool Verbose = false;

        private static void Trace(string message)
        {
            if (Verbose)
            {
                Logger.Info(message);
            }
        }

        private readonly object sync = new object();       // guards lifecycle fields
        private readonly object writeLock = new object();  // serialises pipe writes
        private readonly int pid;

        private NamedPipeClientStream pipe;
        private Thread worker;
        private int generation;            // bumped to retire the current worker/connection
        private string applicationId;
        private PresenceModel lastPresence;
        private bool hasPresence;
        private volatile bool ready;       // READY received on the current connection
        private bool disposed;

        public DiscordPresenceService()
        {
            try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { pid = 0; }
        }

        public bool IsInitialized
        {
            get { lock (sync) { return !disposed && worker != null && !string.IsNullOrEmpty(applicationId); } }
        }

        // ---- public API -----------------------------------------------------

        public void Initialize(string newApplicationId)
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(newApplicationId))
                {
                    Logger.Warn("PlayPresence: Discord Application ID is empty; Rich Presence disabled until set.");
                    StopWorkerLocked();
                    applicationId = null;
                    return;
                }

                var trimmed = newApplicationId.Trim();
                if (worker != null && string.Equals(applicationId, trimmed, StringComparison.Ordinal))
                {
                    return; // already running for this id
                }

                applicationId = trimmed;
                StartWorkerLocked();
                Trace("PlayPresence: Discord IPC worker started.");
            }
        }

        public void SetPresence(PresenceModel model)
        {
            lock (sync)
            {
                lastPresence = model;
                hasPresence = model != null;
            }
            TryWriteActivity();
        }

        public void ClearPresence()
        {
            lock (sync)
            {
                lastPresence = null;
                hasPresence = false;
            }
            TryWriteActivity(); // sends activity:null while keeping the connection
        }

        public void Reconnect()
        {
            lock (sync)
            {
                if (disposed || string.IsNullOrEmpty(applicationId))
                {
                    return;
                }
                Trace("PlayPresence: manual Discord reconnect requested.");
                StartWorkerLocked();
            }
        }

        public void Shutdown()
        {
            lock (sync)
            {
                StopWorkerLocked();
                applicationId = null;
                lastPresence = null;
                hasPresence = false;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                StopWorkerLocked();
            }
        }

        // ---- worker lifecycle (call under sync) ----------------------------

        private void StartWorkerLocked()
        {
            StopWorkerLocked();
            var gen = generation;
            var t = new Thread(() => WorkerLoop(gen)) { IsBackground = true, Name = "PlayPresence-DiscordIPC" };
            worker = t;
            t.Start();
        }

        private void StopWorkerLocked()
        {
            generation++;     // any live worker will see the mismatch and exit
            ready = false;
            worker = null;
            ClosePipeQuietly();
        }

        // ---- worker thread --------------------------------------------------

        private void WorkerLoop(int myGen)
        {
            while (IsCurrent(myGen))
            {
                string appId;
                lock (sync) { appId = applicationId; }
                if (string.IsNullOrEmpty(appId))
                {
                    return;
                }

                if (TryConnectAndHandshake(appId, myGen))
                {
                    ReadLoop(myGen);
                }

                ready = false;
                ClosePipeForGen(myGen);
                if (!SleepUnlessRetired(myGen, 3000))
                {
                    return;
                }
            }
        }

        private bool TryConnectAndHandshake(string appId, int myGen)
        {
            for (var i = 0; i <= 9; i++)
            {
                if (!IsCurrent(myGen))
                {
                    return false;
                }

                NamedPipeClientStream p = null;
                try
                {
                    p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                    p.Connect(500);

                    lock (sync)
                    {
                        if (myGen != generation)
                        {
                            SafeDispose(p);
                            return false;
                        }
                        pipe = p;
                    }

                    var handshake = JsonConvert.SerializeObject(new Dictionary<string, object>
                    {
                        ["v"] = 1,
                        ["client_id"] = appId
                    });
                    WriteFrame(OpHandshake, handshake);
                    Trace("PlayPresence: Discord IPC connected on pipe discord-ipc-" + i + "; handshake sent.");
                    return true;
                }
                catch (Exception)
                {
                    SafeDispose(p);
                    lock (sync) { if (pipe == p) { pipe = null; } }
                }
            }

            return false;
        }

        private void ReadLoop(int myGen)
        {
            var header = new byte[8];
            while (IsCurrent(myGen))
            {
                NamedPipeClientStream p;
                lock (sync) { p = pipe; }
                if (p == null)
                {
                    return;
                }

                try
                {
                    if (!ReadExact(p, header, 8))
                    {
                        return;
                    }
                    var op = BitConverter.ToInt32(header, 0);
                    var len = BitConverter.ToInt32(header, 4);
                    if (len < 0 || len > 1024 * 1024)
                    {
                        return; // sanity guard against a desynced pipe
                    }
                    var payload = len > 0 ? new byte[len] : new byte[0];
                    if (len > 0 && !ReadExact(p, payload, len))
                    {
                        return;
                    }
                    HandleFrame(op, Encoding.UTF8.GetString(payload), myGen);
                }
                catch (Exception)
                {
                    return; // treated as a disconnect; worker loop will reconnect
                }
            }
        }

        private void HandleFrame(int op, string json, int myGen)
        {
            if (op == OpPing)
            {
                try { WriteFrame(OpPong, json); } catch { /* ignore */ }
                return;
            }
            if (op == OpClose)
            {
                Trace("PlayPresence: Discord closed the IPC connection.");
                ready = false;
                return;
            }
            if (op != OpFrame)
            {
                return;
            }

            if (!ready && json.IndexOf("\"READY\"", StringComparison.Ordinal) >= 0)
            {
                ready = true;
                Trace("PlayPresence: Discord IPC ready" + ExtractUser(json) + ".");
                TryWriteActivity();
            }
            else if (json.IndexOf("\"evt\":\"ERROR\"", StringComparison.Ordinal) >= 0)
            {
                Logger.Warn("PlayPresence: Discord returned an error frame: " + Truncate(json, 300));
            }
        }

        // ---- presence push --------------------------------------------------

        private void TryWriteActivity()
        {
            if (!ready)
            {
                return; // will be pushed automatically once READY arrives
            }

            PresenceModel model;
            bool present;
            lock (sync)
            {
                model = lastPresence;
                present = hasPresence;
            }

            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["cmd"] = "SET_ACTIVITY",
                    ["args"] = new Dictionary<string, object>
                    {
                        ["pid"] = pid,
                        ["activity"] = present ? BuildActivity(model) : null
                    },
                    ["nonce"] = Guid.NewGuid().ToString()
                };
                WriteFrame(OpFrame, JsonConvert.SerializeObject(payload));
                Trace("PlayPresence: SET_ACTIVITY sent (" + (present ? ("'" + (model.Details ?? string.Empty) + "'") : "cleared") + ").");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: failed to send activity (non-fatal).");
            }
        }

        /// <summary>
        /// Builds the Discord "activity" object from the presence model. Pure and side-effect free
        /// so it can be unit tested without a live pipe.
        /// </summary>
        public static Dictionary<string, object> BuildActivity(PresenceModel model)
        {
            var activity = new Dictionary<string, object>();

            var details = ClampBytes(model.Details, MaxFieldBytes);
            var state = ClampBytes(model.State, MaxFieldBytes);
            if (!string.IsNullOrEmpty(details)) { activity["details"] = details; }
            if (!string.IsNullOrEmpty(state)) { activity["state"] = state; }

            if (model.StartTimestampUtc.HasValue)
            {
                var startUtc = DateTime.SpecifyKind(model.StartTimestampUtc.Value, DateTimeKind.Utc);
                activity["timestamps"] = new Dictionary<string, object>
                {
                    ["start"] = new DateTimeOffset(startUtc).ToUnixTimeMilliseconds()
                };
            }

            var largeKey = ClampKey(model.LargeImageKey);
            var smallKey = ClampKey(model.SmallImageKey);
            if (!string.IsNullOrEmpty(largeKey) || !string.IsNullOrEmpty(smallKey))
            {
                var assets = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(largeKey)) { assets["large_image"] = largeKey; }
                var largeText = ClampBytes(model.LargeImageText, MaxFieldBytes);
                if (!string.IsNullOrEmpty(largeText)) { assets["large_text"] = largeText; }
                if (!string.IsNullOrEmpty(smallKey)) { assets["small_image"] = smallKey; }
                var smallText = ClampBytes(model.SmallImageText, MaxFieldBytes);
                if (!string.IsNullOrEmpty(smallText)) { assets["small_text"] = smallText; }
                activity["assets"] = assets;
            }

            if (model.Buttons != null && model.Buttons.Count > 0)
            {
                var count = Math.Min(model.Buttons.Count, PresenceModel.MaxButtons);
                var buttons = new List<Dictionary<string, object>>();
                for (var i = 0; i < count; i++)
                {
                    var b = model.Buttons[i];
                    if (b == null || string.IsNullOrEmpty(b.Label) || string.IsNullOrEmpty(b.Url))
                    {
                        continue;
                    }
                    buttons.Add(new Dictionary<string, object>
                    {
                        ["label"] = ClampBytes(b.Label, MaxButtonLabelBytes),
                        ["url"] = b.Url
                    });
                }
                if (buttons.Count > 0)
                {
                    activity["buttons"] = buttons;
                }
            }

            return activity;
        }

        // ---- low-level pipe I/O --------------------------------------------

        private void WriteFrame(int opcode, string json)
        {
            NamedPipeClientStream p;
            lock (sync) { p = pipe; }
            if (p == null)
            {
                throw new IOException("Discord pipe is not connected.");
            }

            var body = Encoding.UTF8.GetBytes(json);
            var buffer = new byte[8 + body.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(opcode), 0, buffer, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(body.Length), 0, buffer, 4, 4);
            Buffer.BlockCopy(body, 0, buffer, 8, body.Length);

            lock (writeLock)
            {
                p.Write(buffer, 0, buffer.Length);
                p.Flush();
            }
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var n = stream.Read(buffer, offset, count - offset);
                if (n <= 0)
                {
                    return false;
                }
                offset += n;
            }
            return true;
        }

        private bool IsCurrent(int myGen)
        {
            lock (sync) { return !disposed && myGen == generation; }
        }

        private bool SleepUnlessRetired(int myGen, int milliseconds)
        {
            var slept = 0;
            while (slept < milliseconds)
            {
                if (!IsCurrent(myGen))
                {
                    return false;
                }
                Thread.Sleep(100);
                slept += 100;
            }
            return IsCurrent(myGen);
        }

        private void ClosePipeForGen(int myGen)
        {
            lock (sync)
            {
                if (myGen != generation)
                {
                    return; // a newer connection owns the pipe now
                }
            }
            ClosePipeQuietly();
        }

        private void ClosePipeQuietly()
        {
            NamedPipeClientStream p;
            lock (sync) { p = pipe; pipe = null; }
            SafeDispose(p);
        }

        private static void SafeDispose(NamedPipeClientStream p)
        {
            if (p == null)
            {
                return;
            }
            try { p.Dispose(); } catch { /* ignore */ }
        }

        // ---- helpers --------------------------------------------------------

        private static string ExtractUser(string json)
        {
            try
            {
                var token = JsonConvert.DeserializeObject<JToken>(json);
                var name = token?["data"]?["user"]?["username"]?.ToString();
                return string.IsNullOrEmpty(name) ? string.Empty : " for user '" + name + "'";
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value;
            }
            return value.Substring(0, max);
        }

        /// <summary>Clamps a string to a maximum number of UTF-8 bytes without splitting a char.</summary>
        private static string ClampBytes(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            {
                return value;
            }
            var trimmed = value;
            while (trimmed.Length > 0 && Encoding.UTF8.GetByteCount(trimmed) > maxBytes)
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }
            return trimmed;
        }

        private static string ClampKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return key;
            }
            return key.Length <= MaxAssetKeyLength ? key : key.Substring(0, MaxAssetKeyLength);
        }
    }
}
