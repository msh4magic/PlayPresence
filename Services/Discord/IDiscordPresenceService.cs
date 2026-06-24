using PlayPresence.Models;

namespace PlayPresence.Services.Discord
{
    /// <summary>
    /// Abstraction over the Discord RPC client. Keeping this behind an interface lets the rest of
    /// the plugin depend only on <see cref="PresenceModel"/> and keeps the concrete library
    /// (Lachee DiscordRichPresence) isolated to a single, replaceable implementation.
    /// </summary>
    public interface IDiscordPresenceService
    {
        /// <summary>True once a client exists and has been initialized.</summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Initializes (or re-initializes) the Discord client for the given application id.
        /// Safe to call repeatedly; a changed id transparently rebuilds the client.
        /// </summary>
        void Initialize(string applicationId);

        /// <summary>Pushes a presence to Discord. No-op (logged) if not initialized.</summary>
        void SetPresence(PresenceModel model);

        /// <summary>Clears any active presence without tearing down the connection.</summary>
        void ClearPresence();

        /// <summary>Forces a clean reconnect and re-applies the most recent presence.</summary>
        void Reconnect();

        /// <summary>Tears down the client and releases the IPC handle.</summary>
        void Shutdown();
    }
}
