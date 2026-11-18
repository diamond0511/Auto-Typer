using System;
using System.Text;
using System.Windows.Forms;

namespace AutoTyper
{
    /// <summary>A global hotkey: one key plus at least one modifier.</summary>
    internal sealed class Hotkey
    {
        public Keys Key;
        public bool Ctrl;
        public bool Alt;
        public bool Shift;
        public bool Win;

        public Hotkey()
        {
        }

        public Hotkey(Keys key, bool ctrl, bool alt, bool shift, bool win)
        {
            Key = key;
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
            Win = win;
        }

        public Hotkey Clone()
        {
            return new Hotkey(Key, Ctrl, Alt, Shift, Win);
        }

        /// <summary>
        /// A modifier is required: a bare key would be swallowed system-wide, which would
        /// make the machine unusable.
        /// </summary>
        public bool IsValid
        {
            get { return Key != Keys.None && (Ctrl || Alt || Shift || Win); }
        }

        /// <summary>
        /// True if Auto Typer's own typing would fire this hotkey. Capitals and shifted
        /// punctuation are produced by genuinely holding Shift, and a registered hotkey
        /// responds to injected keystrokes exactly as it does to a real keyboard, so a
        /// combination whose only modifier is Shift goes off the first time that character
        /// appears in the text being typed. Ctrl, Alt and Win are never held during a run,
        /// which makes any combination including one of them safe.
        /// </summary>
        public bool TriggeredByTyping
        {
            get { return IsValid && Shift && !Ctrl && !Alt && !Win; }
        }

        public uint Modifiers
        {
            get
            {
                uint m = 0;
                if (Alt) m |= NativeMethods.MOD_ALT;
                if (Ctrl) m |= NativeMethods.MOD_CONTROL;
                if (Shift) m |= NativeMethods.MOD_SHIFT;
                if (Win) m |= NativeMethods.MOD_WIN;
                return m;
            }
        }

        public bool SameAs(Hotkey other)
        {
            if (other == null) return false;
            return Key == other.Key && Ctrl == other.Ctrl && Alt == other.Alt
                && Shift == other.Shift && Win == other.Win;
        }

        public override string ToString()
        {
            if (!IsValid) return "(not set)";
            StringBuilder sb = new StringBuilder();
            if (Ctrl) sb.Append("Ctrl+");
            if (Alt) sb.Append("Alt+");
            if (Shift) sb.Append("Shift+");
            if (Win) sb.Append("Win+");
            sb.Append(FriendlyKeyName(Key));
            return sb.ToString();
        }

        public static string FriendlyKeyName(Keys k)
        {
            switch (k)
            {
                case Keys.None: return "?";
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Space: return "Space";
                case Keys.Return: return "Enter";
                case Keys.Escape: return "Esc";
                case Keys.Prior: return "PageUp";
                case Keys.Next: return "PageDown";
                case Keys.Back: return "Backspace";
                case Keys.Capital: return "CapsLock";
            }

            if (k >= Keys.D0 && k <= Keys.D9)
                return ((char)('0' + (k - Keys.D0))).ToString();

            return k.ToString();
        }

        // ---- persistence -------------------------------------------------

        /// <summary>Round-trips as e.g. "Ctrl+Alt+V". Returns <paramref name="fallback"/> on anything unparseable.</summary>
        public static Hotkey Parse(string text, Hotkey fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;

            Hotkey hk = new Hotkey();
            string[] parts = text.Split('+');

            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;

                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) { hk.Ctrl = true; continue; }
                if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) { hk.Alt = true; continue; }
                if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) { hk.Shift = true; continue; }
                if (p.Equals("Win", StringComparison.OrdinalIgnoreCase)) { hk.Win = true; continue; }

                Keys parsed;
                if (!TryParseKeyName(p, out parsed)) return fallback;
                hk.Key = parsed;
            }

            return hk.IsValid ? hk : fallback;
        }

        static bool TryParseKeyName(string name, out Keys key)
        {
            key = Keys.None;

            // Reverse of FriendlyKeyName for the punctuation / renamed cases.
            switch (name)
            {
                case "`": key = Keys.Oemtilde; return true;
                case "-": key = Keys.OemMinus; return true;
                case "=": key = Keys.Oemplus; return true;
                case "[": key = Keys.OemOpenBrackets; return true;
                case "]": key = Keys.OemCloseBrackets; return true;
                case "\\": key = Keys.OemPipe; return true;
                case ";": key = Keys.OemSemicolon; return true;
                case "'": key = Keys.OemQuotes; return true;
                case ",": key = Keys.Oemcomma; return true;
                case ".": key = Keys.OemPeriod; return true;
                case "/": key = Keys.OemQuestion; return true;
            }

            if (name.Equals("Enter", StringComparison.OrdinalIgnoreCase)) { key = Keys.Return; return true; }
            if (name.Equals("Esc", StringComparison.OrdinalIgnoreCase)) { key = Keys.Escape; return true; }
            if (name.Equals("PageUp", StringComparison.OrdinalIgnoreCase)) { key = Keys.Prior; return true; }
            if (name.Equals("PageDown", StringComparison.OrdinalIgnoreCase)) { key = Keys.Next; return true; }
            if (name.Equals("Backspace", StringComparison.OrdinalIgnoreCase)) { key = Keys.Back; return true; }
            if (name.Equals("CapsLock", StringComparison.OrdinalIgnoreCase)) { key = Keys.Capital; return true; }

            if (name.Length == 1 && name[0] >= '0' && name[0] <= '9')
            {
                key = Keys.D0 + (name[0] - '0');
                return true;
            }

            try
            {
                key = (Keys)Enum.Parse(typeof(Keys), name, true);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
