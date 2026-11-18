using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AutoTyper
{
    /// <summary>
    /// User settings, persisted as a flat key=value file. Stored next to the executable when
    /// that folder is writable (portable / USB use), otherwise under %APPDATA%.
    /// </summary>
    internal sealed class AppSettings
    {
        public int StartDelayMs = 1200;

        // The midpoint of this range is the typical gap between keystrokes. 100 ms is what
        // three real typists actually produced (53-74 wpm), so the default reproduces the
        // recordings; moving the range scales the measured rhythm without flattening it.
        //
        // There is a floor to respect as well. Apps built on WinUI/TSF - Windows 11 Notepad
        // is the worst offender - consume injected Unicode packets on a separate thread.
        // Below roughly 60 ms they have not read packet N before packet N+1 overwrites it,
        // and characters come out substituted ("City" -> "iity"). 20-55 ms failed 9 of 9
        // test runs against Store Notepad; 80 ms passed 4 of 4.
        public int MinKeyDelayMs = 80;
        public int MaxKeyDelayMs = 120;

        /// <summary>Below this, slow consumers start garbling input. Used only for warnings.</summary>
        public const int SafeKeyDelayMs = 60;

        /// <summary>
        /// Send characters as real virtual keys, with Shift genuinely pressed, instead of
        /// Unicode packets. Packets carry no physical key, so web forms see an empty
        /// event.code and shiftKey always false, which upsets input masks and validation.
        /// Characters the layout cannot type still fall back to packets.
        /// </summary>
        public bool UseRealKeys = true;

        /// <summary>
        /// Pace the keystrokes from the measured profile in <see cref="RhythmProfile"/>
        /// instead of drawing uniformly at random between the delay bounds.
        /// </summary>
        public bool HumanRhythm = true;

        /// <summary>
        /// Mistype occasionally and correct it with Backspace, at the rate real typists
        /// did (one slip per 46 characters, noticed about two characters later). Off by
        /// default: it is convincing, but it puts a wrong character into the target field
        /// for a moment, which is not always acceptable.
        /// </summary>
        public bool MakeMistakes = false;

        /// <summary>
        /// How long each key stays down, used only when the measured rhythm is off. With
        /// it on, dwell comes from the profile, where the median is 120 ms.
        /// </summary>
        public int KeyHoldMinMs = 30;
        public int KeyHoldMaxMs = 75;

        public bool NewlineAsEnter = true;
        public bool TabAsTab = true;
        public bool StripTrailingNewline = true;
        public bool StopOnFocusChange = true;

        /// <summary>
        /// Stop the run the moment any mouse button goes down. The keyboard and the mouse
        /// cannot be used at the same time while a run is in progress, so a click is taken
        /// as "I want control back now". Movement is not watched: nudging the mouse, or a
        /// remote session syncing the cursor, is not a request to stop.
        /// </summary>
        public bool StopOnMouseClick = true;

        public bool ShowTrayIcon = true;
        public bool ShowNotifications = true;

        public Hotkey TypeHotkey = new Hotkey(Keys.V, true, true, false, false);
        public Hotkey AbortHotkey = new Hotkey(Keys.X, true, true, false, false);
        public Hotkey SettingsHotkey = new Hotkey(Keys.S, true, true, false, false);
        public Hotkey QuitHotkey = new Hotkey(Keys.Q, true, true, false, false);

        public bool FirstRun = true;

        /// <summary>
        /// Set by <see cref="Normalize"/> when a stored hotkey had to be replaced because
        /// Auto Typer's own typing would fire it. Not persisted: it exists so that the tray
        /// can explain the change once, at startup.
        /// </summary>
        public string HotkeyWarning;

        const string FileName = "autotyper.ini";

        public AppSettings Clone()
        {
            AppSettings c = new AppSettings();
            c.StartDelayMs = StartDelayMs;
            c.MinKeyDelayMs = MinKeyDelayMs;
            c.MaxKeyDelayMs = MaxKeyDelayMs;
            c.UseRealKeys = UseRealKeys;
            c.HumanRhythm = HumanRhythm;
            c.MakeMistakes = MakeMistakes;
            c.KeyHoldMinMs = KeyHoldMinMs;
            c.KeyHoldMaxMs = KeyHoldMaxMs;
            c.NewlineAsEnter = NewlineAsEnter;
            c.TabAsTab = TabAsTab;
            c.StripTrailingNewline = StripTrailingNewline;
            c.StopOnFocusChange = StopOnFocusChange;
            c.StopOnMouseClick = StopOnMouseClick;
            c.ShowTrayIcon = ShowTrayIcon;
            c.ShowNotifications = ShowNotifications;
            c.TypeHotkey = TypeHotkey.Clone();
            c.AbortHotkey = AbortHotkey.Clone();
            c.SettingsHotkey = SettingsHotkey.Clone();
            c.QuitHotkey = QuitHotkey.Clone();
            c.FirstRun = FirstRun;
            return c;
        }

        public void CopyFrom(AppSettings o)
        {
            StartDelayMs = o.StartDelayMs;
            MinKeyDelayMs = o.MinKeyDelayMs;
            MaxKeyDelayMs = o.MaxKeyDelayMs;
            UseRealKeys = o.UseRealKeys;
            HumanRhythm = o.HumanRhythm;
            MakeMistakes = o.MakeMistakes;
            KeyHoldMinMs = o.KeyHoldMinMs;
            KeyHoldMaxMs = o.KeyHoldMaxMs;
            NewlineAsEnter = o.NewlineAsEnter;
            TabAsTab = o.TabAsTab;
            StripTrailingNewline = o.StripTrailingNewline;
            StopOnFocusChange = o.StopOnFocusChange;
            StopOnMouseClick = o.StopOnMouseClick;
            ShowTrayIcon = o.ShowTrayIcon;
            ShowNotifications = o.ShowNotifications;
            TypeHotkey = o.TypeHotkey.Clone();
            AbortHotkey = o.AbortHotkey.Clone();
            SettingsHotkey = o.SettingsHotkey.Clone();
            QuitHotkey = o.QuitHotkey.Clone();
            FirstRun = o.FirstRun;
        }

        public void Normalize()
        {
            if (StartDelayMs < 0) StartDelayMs = 0;
            if (StartDelayMs > 60000) StartDelayMs = 60000;
            if (MinKeyDelayMs < 0) MinKeyDelayMs = 0;
            if (MinKeyDelayMs > 5000) MinKeyDelayMs = 5000;
            if (MaxKeyDelayMs < 0) MaxKeyDelayMs = 0;
            if (MaxKeyDelayMs > 5000) MaxKeyDelayMs = 5000;
            if (MaxKeyDelayMs < MinKeyDelayMs) MaxKeyDelayMs = MinKeyDelayMs;

            if (KeyHoldMinMs < 1) KeyHoldMinMs = 1;
            if (KeyHoldMinMs > 500) KeyHoldMinMs = 500;
            if (KeyHoldMaxMs < 1) KeyHoldMaxMs = 1;
            if (KeyHoldMaxMs > 500) KeyHoldMaxMs = 500;
            if (KeyHoldMaxMs < KeyHoldMinMs) KeyHoldMaxMs = KeyHoldMinMs;

            if (TypeHotkey == null || !TypeHotkey.IsValid)
                TypeHotkey = new Hotkey(Keys.V, true, true, false, false);
            if (AbortHotkey == null) AbortHotkey = new Hotkey();
            if (SettingsHotkey == null) SettingsHotkey = new Hotkey();
            if (QuitHotkey == null) QuitHotkey = new Hotkey();

            // A hotkey whose only modifier is Shift is one Auto Typer types itself: the
            // first capital in the text fires it, and the run stops, restarts or quits part
            // way through. The settings dialog refuses them, but the file can be hand-edited
            // and older files may already hold one, so they are replaced here as well.
            List<string> repaired = new List<string>();
            TypeHotkey = Detypable(TypeHotkey, new Hotkey(Keys.V, true, true, false, false), "type clipboard", repaired);
            AbortHotkey = Detypable(AbortHotkey, new Hotkey(Keys.X, true, true, false, false), "stop typing", repaired);
            SettingsHotkey = Detypable(SettingsHotkey, new Hotkey(Keys.S, true, true, false, false), "open settings", repaired);
            QuitHotkey = Detypable(QuitHotkey, new Hotkey(Keys.Q, true, true, false, false), "quit", repaired);

            HotkeyWarning = (repaired.Count > 0) ? string.Join("\r\n", repaired.ToArray()) : null;
        }

        /// <summary>
        /// Swaps a hotkey that Auto Typer's own keystrokes would fire for one they cannot,
        /// and notes the swap. Anything else is returned untouched.
        /// </summary>
        static Hotkey Detypable(Hotkey hk, Hotkey safe, string what, List<string> repaired)
        {
            if (hk == null || !hk.TriggeredByTyping) return hk;
            repaired.Add("  " + hk + "   (" + what + ")   is now   " + safe);
            return safe;
        }

        // ---- storage -----------------------------------------------------

        static string _resolvedPath;

        public static string SettingsPath
        {
            get
            {
                if (_resolvedPath != null) return _resolvedPath;

                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                string candidate = Path.Combine(exeDir, FileName);

                if (IsWritable(exeDir))
                {
                    _resolvedPath = candidate;
                }
                else
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "AutoTyper");
                    _resolvedPath = Path.Combine(dir, FileName);
                }

                return _resolvedPath;
            }
        }

        static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".tmp");
                using (FileStream fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write))
                {
                    fs.WriteByte(0);
                }
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();

            try
            {
                string path = SettingsPath;
                if (!File.Exists(path))
                {
                    s.Normalize();
                    return s;
                }

                Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                s.StartDelayMs = GetInt(map, "StartDelayMs", s.StartDelayMs);
                s.MinKeyDelayMs = GetInt(map, "MinKeyDelayMs", s.MinKeyDelayMs);
                s.MaxKeyDelayMs = GetInt(map, "MaxKeyDelayMs", s.MaxKeyDelayMs);

                s.UseRealKeys = GetBool(map, "UseRealKeys", s.UseRealKeys);
                s.HumanRhythm = GetBool(map, "HumanRhythm", s.HumanRhythm);
                s.MakeMistakes = GetBool(map, "MakeMistakes", s.MakeMistakes);
                s.KeyHoldMinMs = GetInt(map, "KeyHoldMinMs", s.KeyHoldMinMs);
                s.KeyHoldMaxMs = GetInt(map, "KeyHoldMaxMs", s.KeyHoldMaxMs);

                s.NewlineAsEnter = GetBool(map, "NewlineAsEnter", s.NewlineAsEnter);
                s.TabAsTab = GetBool(map, "TabAsTab", s.TabAsTab);
                s.StripTrailingNewline = GetBool(map, "StripTrailingNewline", s.StripTrailingNewline);
                s.StopOnFocusChange = GetBool(map, "StopOnFocusChange", s.StopOnFocusChange);
                // StopOnMouse is the older key, from when movement counted as well.
                s.StopOnMouseClick = GetBool(map, "StopOnMouse", s.StopOnMouseClick);
                s.StopOnMouseClick = GetBool(map, "StopOnMouseClick", s.StopOnMouseClick);

                s.ShowTrayIcon = GetBool(map, "ShowTrayIcon", s.ShowTrayIcon);
                s.ShowNotifications = GetBool(map, "ShowNotifications", s.ShowNotifications);

                s.TypeHotkey = GetHotkey(map, "TypeHotkey", s.TypeHotkey);
                s.AbortHotkey = GetHotkey(map, "AbortHotkey", s.AbortHotkey);
                s.SettingsHotkey = GetHotkey(map, "SettingsHotkey", s.SettingsHotkey);
                s.QuitHotkey = GetHotkey(map, "QuitHotkey", s.QuitHotkey);

                s.FirstRun = GetBool(map, "FirstRun", false);
            }
            catch (Exception)
            {
                // A corrupt or unreadable file must not stop the app from starting.
                s = new AppSettings();
            }

            s.Normalize();
            return s;
        }

        public bool Save()
        {
            try
            {
                string path = SettingsPath;
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Auto Typer settings");
                sb.AppendLine("# Hotkeys look like: Ctrl+Alt+V   (leave blank to disable)");
                sb.AppendLine();
                sb.AppendLine("TypeHotkey = " + HotkeyText(TypeHotkey));
                sb.AppendLine("AbortHotkey = " + HotkeyText(AbortHotkey));
                sb.AppendLine("SettingsHotkey = " + HotkeyText(SettingsHotkey));
                sb.AppendLine("QuitHotkey = " + HotkeyText(QuitHotkey));
                sb.AppendLine();
                sb.AppendLine("StartDelayMs = " + StartDelayMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("MinKeyDelayMs = " + MinKeyDelayMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("MaxKeyDelayMs = " + MaxKeyDelayMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("KeyHoldMinMs = " + KeyHoldMinMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("KeyHoldMaxMs = " + KeyHoldMaxMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
                sb.AppendLine("UseRealKeys = " + Bool(UseRealKeys));
                sb.AppendLine("HumanRhythm = " + Bool(HumanRhythm));
                sb.AppendLine("MakeMistakes = " + Bool(MakeMistakes));
                sb.AppendLine("NewlineAsEnter = " + Bool(NewlineAsEnter));
                sb.AppendLine("TabAsTab = " + Bool(TabAsTab));
                sb.AppendLine("StripTrailingNewline = " + Bool(StripTrailingNewline));
                sb.AppendLine("StopOnFocusChange = " + Bool(StopOnFocusChange));
                sb.AppendLine("StopOnMouseClick = " + Bool(StopOnMouseClick));
                sb.AppendLine();
                sb.AppendLine("ShowTrayIcon = " + Bool(ShowTrayIcon));
                sb.AppendLine("ShowNotifications = " + Bool(ShowNotifications));
                sb.AppendLine();
                sb.AppendLine("FirstRun = " + Bool(FirstRun));

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static string HotkeyText(Hotkey hk)
        {
            return (hk != null && hk.IsValid) ? hk.ToString() : "";
        }

        static string Bool(bool b)
        {
            return b ? "true" : "false";
        }

        static int GetInt(Dictionary<string, string> map, string key, int fallback)
        {
            string v;
            int parsed;
            if (map.TryGetValue(key, out v) &&
                int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return fallback;
        }

        static bool GetBool(Dictionary<string, string> map, string key, bool fallback)
        {
            string v;
            if (!map.TryGetValue(key, out v)) return fallback;

            v = v.Trim();
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ||
                v.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0" ||
                v.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;

            return fallback;
        }

        static Hotkey GetHotkey(Dictionary<string, string> map, string key, Hotkey fallback)
        {
            string v;
            if (!map.TryGetValue(key, out v)) return fallback;
            if (v.Trim().Length == 0) return new Hotkey();   // explicitly disabled
            return Hotkey.Parse(v, fallback);
        }
    }
}
