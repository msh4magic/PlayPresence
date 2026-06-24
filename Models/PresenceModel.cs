using System;
using System.Collections.Generic;

namespace PlayPresence.Models
{
    /// <summary>A single optional Rich Presence button.</summary>
    public sealed class PresenceButton
    {
        public string Label { get; set; }
        public string Url { get; set; }
    }

    /// <summary>
    /// A Discord-agnostic description of the desired Rich Presence state. The presence builder
    /// produces this; the Discord service is the only component that knows how to translate it
    /// into the concrete library types. This keeps presence composition fully unit-testable.
    /// </summary>
    public sealed class PresenceModel
    {
        public string Details { get; set; }
        public string State { get; set; }

        public string LargeImageKey { get; set; }
        public string LargeImageText { get; set; }
        public string SmallImageKey { get; set; }
        public string SmallImageText { get; set; }

        /// <summary>When set, Discord shows an elapsed timer counting up from this UTC instant.</summary>
        public DateTime? StartTimestampUtc { get; set; }

        public List<PresenceButton> Buttons { get; set; } = new List<PresenceButton>();

        /// <summary>Discord hard limits, applied defensively before sending.</summary>
        public const int MaxFieldLength = 128;
        public const int MaxButtonLabelLength = 32;
        public const int MaxButtons = 2;
    }
}
