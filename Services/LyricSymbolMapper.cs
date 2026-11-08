using System;
using System.Collections.Generic;
using System.Linq;

namespace LmenV2.Services
{
    /// <summary>
    /// Word-to-Symbol mapping inspired by Verci (https://www.verci.xyz/) Fisheye Visual mode.
    /// Pairs lyric words with matched emoji symbols for iconic visual aesthetics.
    /// </summary>
    public static class LyricSymbolMapper
    {
        private static readonly Dictionary<string, string> WordToSymbol = new(StringComparer.OrdinalIgnoreCase)
        {
            // Time, Clocks & Waiting (Matches Verci "wait ⌛", "minute ⏱")
            { "wait", "⌛" }, { "waiting", "⌛" }, { "waited", "⌛" },
            { "minute", "⏱" }, { "minutes", "⏱" },
            { "second", "⏱" }, { "seconds", "⏱" },
            { "hour", "⏱" }, { "hours", "⏱" },
            { "time", "⏱" }, { "times", "⏱" },
            { "clock", "⏰" }, { "clocks", "⏰" },
            { "forever", "♾️" }, { "always", "♾️" },
            { "never", "🚫" }, { "late", "⏱" },

            // Perception, Speech & Communication (Matches Verci "see 👁", "telling 💬")
            { "see", "👁" }, { "seeing", "👁" }, { "saw", "👁" }, { "seen", "👁" },
            { "look", "👁" }, { "looking", "👁" }, { "looked", "👁" },
            { "eyes", "👁" }, { "eye", "👁" },
            { "watch", "👁" }, { "watching", "👁" },
            { "tell", "💬" }, { "telling", "💬" }, { "told", "💬" },
            { "say", "💬" }, { "saying", "💬" }, { "said", "💬" },
            { "talk", "💬" }, { "talking", "💬" }, { "talked", "💬" },
            { "speak", "💬" }, { "speaking", "💬" }, { "spoke", "💬" },
            { "words", "💬" }, { "word", "💬" },
            { "call", "📞" }, { "calling", "📞" }, { "called", "📞" },
            { "phone", "📱" }, { "message", "💬" },
            { "listen", "👂" }, { "hear", "👂" }, { "hearing", "👂" }, { "heard", "👂" },
            { "whisper", "🤫" }, { "silent", "🤫" }, { "silence", "🤫" },
            { "shout", "📢" }, { "scream", "📢" }, { "loud", "🔊" },

            // Emotion, Heart, Love & Soul
            { "love", "❤️" }, { "loving", "❤️" }, { "loved", "❤️" }, { "lover", "❤️" }, { "lovers", "❤️" },
            { "heart", "❤️" }, { "hearts", "❤️" },
            { "baby", "✨" }, { "darling", "✨" }, { "honey", "🍯" },
            { "broken", "💔" }, { "break", "💔" }, { "breaking", "💔" }, { "broke", "💔" },
            { "hurt", "💔" }, { "pain", "🥀" }, { "wound", "🥀" },
            { "cry", "💧" }, { "crying", "💧" }, { "cried", "💧" },
            { "tears", "💧" }, { "tear", "💧" },
            { "kiss", "💋" }, { "kisses", "💋" }, { "kissed", "💋" }, { "kissing", "💋" },
            { "touch", "✨" }, { "feel", "💫" }, { "feeling", "💫" }, { "feels", "💫" },
            { "smile", "😊" }, { "smiling", "😊" }, { "happy", "✨" },
            { "laugh", "😄" }, { "laughing", "😄" },
            { "alone", "👤" }, { "lonely", "👤" }, { "sorry", "🥺" },
            { "dream", "💭" }, { "dreams", "💭" }, { "dreaming", "💭" }, { "dreamt", "💭" },
            { "sleep", "💤" }, { "sleeping", "💤" }, { "awake", "👁" },

            // Elements, Astronomy, Weather & Nature
            { "fire", "🔥" }, { "burn", "🔥" }, { "burning", "🔥" }, { "burned", "🔥" },
            { "flame", "🔥" }, { "flames", "🔥" }, { "hot", "🔥" },
            { "light", "💡" }, { "lights", "💡" }, { "lighting", "💡" },
            { "shine", "✨" }, { "shining", "✨" }, { "glow", "✨" }, { "glowing", "✨" },
            { "sun", "☀️" }, { "sunshine", "☀️" }, { "sunny", "☀️" },
            { "day", "☀️" }, { "days", "☀️" }, { "morning", "🌅" },
            { "night", "🌙" }, { "nights", "🌙" }, { "dark", "🌙" }, { "darkness", "🌙" },
            { "moon", "🌙" }, { "midnight", "🌌" }, { "shadow", "👤" }, { "shadows", "👤" },
            { "star", "⭐" }, { "stars", "⭐" }, { "starlight", "⭐" },
            { "sky", "☁️" }, { "cloud", "☁️" }, { "clouds", "☁️" },
            { "rain", "🌧" }, { "raining", "🌧" }, { "storm", "⛈" }, { "storms", "⛈" },
            { "thunder", "⚡" }, { "lightning", "⚡" }, { "electric", "⚡" },
            { "cold", "❄️" }, { "ice", "❄️" }, { "snow", "❄️" }, { "winter", "❄️" }, { "freeze", "❄️" },
            { "wind", "🍃" }, { "breeze", "🍃" }, { "air", "💨" },
            { "water", "🌊" }, { "ocean", "🌊" }, { "sea", "🌊" }, { "river", "🌊" },
            { "wave", "🌊" }, { "waves", "🌊" }, { "drown", "🌊" },

            // Movement, Travel, Objects & Urban
            { "fly", "🕊️" }, { "flying", "🕊️" }, { "wings", "🪽" }, { "bird", "🕊️" }, { "birds", "🕊️" },
            { "free", "🕊️" }, { "freedom", "🕊️" }, { "peace", "✌️" },
            { "car", "🚗" }, { "drive", "🚗" }, { "driving", "🚗" }, { "drove", "🚗" }, { "ride", "🚗" }, { "riding", "🚗" },
            { "road", "🛣️" }, { "street", "🛣️" }, { "streets", "🛣️" }, { "highway", "🛣️" },
            { "city", "🏙️" }, { "town", "🏘️" },
            { "home", "🏠" }, { "house", "🏠" },
            { "door", "🚪" }, { "doors", "🚪" }, { "window", "🪟" }, { "windows", "🪟" },
            { "key", "🗝️" }, { "keys", "🗝️" }, { "lock", "🔒" }, { "locked", "🔒" },
            { "button", "🔘" }, { "buttons", "🔘" },
            { "run", "🏃" }, { "running", "🏃" }, { "ran", "🏃" },
            { "walk", "🚶" }, { "walking", "🚶" }, { "walked", "🚶" },
            { "dance", "💃" }, { "dancing", "🕺" }, { "danced", "💃" },
            { "music", "🎵" }, { "song", "🎶" }, { "songs", "🎶" },
            { "sing", "🎤" }, { "singing", "🎤" }, { "sang", "🎤" },
            { "sound", "🔊" }, { "beat", "🥁" }, { "beats", "🥁" },
            { "world", "🌍" }, { "earth", "🌍" },
            { "space", "🚀" }, { "rocket", "🚀" },

            // Value, Royalty & Life
            { "money", "💵" }, { "rich", "💰" }, { "cash", "💸" }, { "dollar", "💵" }, { "dollars", "💵" },
            { "gold", "🪙" }, { "golden", "🪙" },
            { "diamond", "💎" }, { "diamonds", "💎" },
            { "crown", "👑" }, { "king", "👑" }, { "queen", "👑" },
            { "magic", "🪄" }, { "magical", "🪄" }, { "wonder", "✨" },
            { "rose", "🌹" }, { "roses", "🌹" }, { "flower", "🌸" }, { "flowers", "💐" },
            { "drink", "🍷" }, { "drinking", "🍷" }, { "wine", "🍷" }, { "cup", "🥤" }, { "glass", "🥂" },
            { "game", "🎮" }, { "play", "🎲" }, { "playing", "🎲" }, { "played", "🎲" },
            { "win", "🏆" }, { "winner", "🏆" }, { "winning", "🏆" }, { "won", "🏆" },
            { "lose", "🥀" }, { "loser", "🥀" }, { "lost", "🥀" },
            { "danger", "⚠️" }, { "wild", "⚡" }, { "power", "⚡" }, { "strong", "💪" },
            { "friend", "🤝" }, { "friends", "🤝" },
            { "party", "🎉" }, { "celebrate", "🥳" },
            { "mind", "🧠" }, { "think", "💭" }, { "thinking", "💭" }, { "thought", "💭" }, { "head", "👤" },
            { "life", "🌟" }, { "alive", "🌟" }, { "live", "🌟" }, { "living", "🌟" }
        };

        /// <summary>
        /// Attempts to find a matching visual symbol for the provided word, ignoring punctuation.
        /// </summary>
        public static string? TryGetSymbol(string? rawWord)
        {
            if (string.IsNullOrWhiteSpace(rawWord)) return null;

            // Strip leading and trailing punctuation and whitespace
            var clean = new string(rawWord.Where(char.IsLetterOrDigit).ToArray()).Trim();
            if (string.IsNullOrEmpty(clean)) return null;

            if (WordToSymbol.TryGetValue(clean, out string? symbol))
            {
                return symbol;
            }

            return null;
        }
    }
}
