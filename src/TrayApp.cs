using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AutoTyper
{
    /// <summary>
    /// The application shell: owns the tray icon, the global hotkeys and the typing engine.
    /// There is no main window, so the process has no taskbar button and no console.
    /// </summary>
    internal sealed class TrayApp : ApplicationContext
    {
        const int HkType = 1;
        const int HkAbort = 2;
        const int HkSettings = 3;
        const int HkQuit = 4;

        readonly AppSettings _settings;
        readonly MessageWindow _window;
        readonly TypingEngine _engine;

        NotifyIcon _tray;
        ToolStripMenuItem _miType;
        ToolStripMenuItem _miStop;
        Icon _icon;
        SettingsForm _settingsForm;
        bool _hotkeysRegistered;

        public TrayApp()
        {
            _settings = AppSettings.Load();

            _window = new MessageWindow();
            _window.HotkeyPressed += OnHotkey;

            _engine = new TypingEngine();
            _engine.Progress += OnEngineProgress;
            _engine.Stopped += OnEngineStopped;

            _icon = LoadIcon();

            BuildTray();
            ApplyTrayVisibility();

            if (_settings.HotkeyWarning != null)
            {
                // Say this before anything else: the saved file held a hotkey that would
                // have stopped a run part way through, and it has just been changed.
                MessageBox.Show(
                    "A saved hotkey used Shift as its only modifier:\r\n\r\n" +
                    _settings.HotkeyWarning + "\r\n\r\n" +
                    "Auto Typer types capitals by holding Shift, so that hotkey fired on the " +
                    "first capital in your text and the run stopped itself part way through.\r\n\r\n" +
                    "It has been changed to the default shown above. Pick something with Ctrl, " +
                    "Alt or Win in Settings if you want a different one.",
                    "Auto Typer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                _settings.Save();
            }

            List<string> failed = RegisterHotkeys();

            if (_settings.FirstRun)
            {
                _settings.FirstRun = false;
                _settings.Save();
                ShowWelcome();
            }

            if (failed.Count > 0)
            {
                MessageBox.Show(
                    "Another program already owns " +
                    (failed.Count == 1 ? "this hotkey:" : "these hotkeys:") + "\r\n\r\n  " +
                    string.Join("\r\n  ", failed.ToArray()) +
                    "\r\n\r\nPick a different combination in Settings.",
                    "Auto Typer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        // ---- tray --------------------------------------------------------

        static Icon LoadIcon()
        {
            try
            {
                Icon extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extracted != null) return extracted;
            }
            catch (Exception)
            {
                // Falls through to the stock icon.
            }

            return SystemIcons.Application;
        }

        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            _miType = new ToolStripMenuItem("Type clipboard now", null, delegate { TriggerType(); });
            _miType.Font = new Font(_miType.Font, FontStyle.Bold);
            menu.Items.Add(_miType);

            _miStop = new ToolStripMenuItem("Stop typing", null, delegate { _engine.Abort(); });
            _miStop.Enabled = false;
            menu.Items.Add(_miStop);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Settings...", null, delegate { ShowSettings(); }));
            menu.Items.Add(new ToolStripMenuItem("Hotkeys", null, delegate { ShowWelcome(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { Quit(); }));

            _tray = new NotifyIcon();
            _tray.Icon = _icon;
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowSettings(); };
            UpdateTooltip("Auto Typer - idle");
        }

        void ApplyTrayVisibility()
        {
            _tray.Visible = _settings.ShowTrayIcon;
        }

        void UpdateTooltip(string text)
        {
            // NotifyIcon.Text throws above 63 characters.
            if (text.Length > 63) text = text.Substring(0, 60) + "...";
            _tray.Text = text;
        }

        void Notify(string title, string body, ToolTipIcon icon)
        {
            if (!_settings.ShowNotifications) return;
            if (!_tray.Visible) return;   // balloons require a visible icon

            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = body;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(3000);
            }
            catch (Exception)
            {
                // Notifications are cosmetic.
            }
        }

        void ShowWelcome()
        {
            string text =
                "Auto Typer runs in the background and types your clipboard as real keystrokes.\r\n\r\n" +
                "  " + Describe(_settings.TypeHotkey) + "   type the clipboard\r\n" +
                "  " + Describe(_settings.AbortHotkey) + "   stop typing\r\n" +
                "  " + Describe(_settings.SettingsHotkey) + "   open settings\r\n" +
                "  " + Describe(_settings.QuitHotkey) + "   quit\r\n\r\n" +
                "Press the type hotkey, then click the field you want filled. Typing begins after " +
                _settings.StartDelayMs.ToString(CultureInfo.CurrentCulture) + " ms.\r\n\r\n" +
                "Once it starts, clicking the mouse stops it, so you can take the machine back " +
                "without reaching for a hotkey.\r\n\r\n" +
                "Settings file:\r\n" + AppSettings.SettingsPath;

            MessageBox.Show(text, "Auto Typer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static string Describe(Hotkey hk)
        {
            return (hk != null && hk.IsValid) ? hk.ToString() : "(not set)";
        }

        // ---- hotkeys -----------------------------------------------------

        /// <summary>Returns the descriptions of any hotkeys that could not be claimed.</summary>
        List<string> RegisterHotkeys()
        {
            List<string> failed = new List<string>();

            if (!TryRegister(HkType, _settings.TypeHotkey)) failed.Add(_settings.TypeHotkey + "  (type clipboard)");
            if (!TryRegister(HkAbort, _settings.AbortHotkey)) failed.Add(_settings.AbortHotkey + "  (stop typing)");
            if (!TryRegister(HkSettings, _settings.SettingsHotkey)) failed.Add(_settings.SettingsHotkey + "  (settings)");
            if (!TryRegister(HkQuit, _settings.QuitHotkey)) failed.Add(_settings.QuitHotkey + "  (quit)");

            _hotkeysRegistered = true;
            return failed;
        }

        bool TryRegister(int id, Hotkey hk)
        {
            if (hk == null || !hk.IsValid) return true;   // intentionally disabled

            // MOD_NOREPEAT stops auto-repeat from queueing a burst of triggers.
            return NativeMethods.RegisterHotKey(
                _window.Handle,
                id,
                hk.Modifiers | NativeMethods.MOD_NOREPEAT,
                (uint)hk.Key);
        }

        void UnregisterHotkeys()
        {
            if (!_hotkeysRegistered) return;

            int[] ids = { HkType, HkAbort, HkSettings, HkQuit };
            for (int i = 0; i < ids.Length; i++)
            {
                NativeMethods.UnregisterHotKey(_window.Handle, ids[i]);
            }

            _hotkeysRegistered = false;
        }

        void OnHotkey(int id)
        {
            switch (id)
            {
                case HkType:
                    // Doubles as a stop, so the abort hotkey is optional.
                    if (_engine.IsRunning) _engine.Abort();
                    else TriggerType();
                    break;

                case HkAbort:
                    _engine.Abort();
                    break;

                case HkSettings:
                    ShowSettings();
                    break;

                case HkQuit:
                    Quit();
                    break;
            }
        }

        // ---- typing ------------------------------------------------------

        void TriggerType()
        {
            if (_engine.IsRunning) return;

            string text = ReadClipboardText();

            if (text == null)
            {
                Notify("Auto Typer", "The clipboard does not contain any text.", ToolTipIcon.Warning);
                return;
            }

            if (_settings.StripTrailingNewline)
            {
                text = text.TrimEnd('\r', '\n');
            }

            if (text.Length == 0)
            {
                Notify("Auto Typer", "The clipboard text is empty.", ToolTipIcon.Warning);
                return;
            }

            if (!_engine.Start(text, _settings)) return;

            _miType.Enabled = false;
            _miStop.Enabled = true;
            UpdateTooltip("Auto Typer - typing " + text.Length.ToString(CultureInfo.CurrentCulture) + " chars");

            Notify("Auto Typer",
                string.Format(CultureInfo.CurrentCulture,
                    "Typing {0:#,0} characters in {1:0.0} s. Click the target field now.",
                    text.Length,
                    _settings.StartDelayMs / 1000.0),
                ToolTipIcon.Info);
        }

        /// <summary>
        /// Reads clipboard text, retrying briefly: another process may hold the clipboard open.
        /// Must run on the STA UI thread.
        /// </summary>
        static string ReadClipboardText()
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (!Clipboard.ContainsText()) return null;

                    string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                    if (string.IsNullOrEmpty(text)) text = Clipboard.GetText();

                    return string.IsNullOrEmpty(text) ? null : text;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(60);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return null;
        }

        void OnEngineProgress(int typed, int total)
        {
            _window.Post(delegate
            {
                int percent = (total > 0) ? (int)(100L * typed / total) : 0;
                UpdateTooltip("Auto Typer - typing " + percent.ToString(CultureInfo.CurrentCulture) + "%");
            });
        }

        void OnEngineStopped(StopReason reason, int typed, int total)
        {
            _window.Post(delegate
            {
                _miType.Enabled = true;
                _miStop.Enabled = false;
                UpdateTooltip("Auto Typer - idle");

                switch (reason)
                {
                    case StopReason.Completed:
                        Notify("Auto Typer",
                            string.Format(CultureInfo.CurrentCulture, "Typed {0:#,0} characters.", total),
                            ToolTipIcon.Info);
                        break;

                    case StopReason.Aborted:
                        Notify("Auto Typer",
                            string.Format(CultureInfo.CurrentCulture, "Stopped after {0:#,0} of {1:#,0} characters.", typed, total),
                            ToolTipIcon.Info);
                        break;

                    case StopReason.FocusLost:
                        Notify("Auto Typer",
                            string.Format(CultureInfo.CurrentCulture,
                                "Stopped at {0:#,0} of {1:#,0}: the target window lost focus.", typed, total),
                            ToolTipIcon.Warning);
                        break;

                    case StopReason.MouseClicked:
                        Notify("Auto Typer",
                            string.Format(CultureInfo.CurrentCulture,
                                "Stopped at {0:#,0} of {1:#,0}: the mouse was clicked.", typed, total),
                            ToolTipIcon.Info);
                        break;

                    case StopReason.Blocked:
                        Notify("Auto Typer",
                            "That window refused synthetic input. Elevated windows need Auto Typer to run as administrator too.",
                            ToolTipIcon.Error);
                        break;

                    default:
                        Notify("Auto Typer", "Typing stopped unexpectedly.", ToolTipIcon.Error);
                        break;
                }
            });
        }

        // ---- settings ----------------------------------------------------

        void ShowSettings()
        {
            if (_settingsForm != null)
            {
                _settingsForm.Activate();
                return;
            }

            // While the dialog is open the hotkeys have to be released, otherwise Windows
            // routes them here instead of to the capture boxes.
            UnregisterHotkeys();

            try
            {
                _settingsForm = new SettingsForm(_settings);
                DialogResult result = _settingsForm.ShowDialog();

                if (result == DialogResult.OK)
                {
                    _settings.CopyFrom(_settingsForm.Result);
                    ApplyTrayVisibility();

                    if (!_settings.Save())
                    {
                        MessageBox.Show(
                            "Settings could not be written to:\r\n" + AppSettings.SettingsPath +
                            "\r\n\r\nThey will apply until Auto Typer exits.",
                            "Auto Typer",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                }
            }
            finally
            {
                if (_settingsForm != null)
                {
                    _settingsForm.Dispose();
                    _settingsForm = null;
                }

                List<string> failed = RegisterHotkeys();
                if (failed.Count > 0)
                {
                    Notify("Auto Typer", "In use by another program: " + string.Join(", ", failed.ToArray()),
                        ToolTipIcon.Warning);
                }
            }
        }

        // ---- shutdown ----------------------------------------------------

        void Quit()
        {
            _engine.Abort();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UnregisterHotkeys();

                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }

                if (_settingsForm != null)
                {
                    _settingsForm.Dispose();
                    _settingsForm = null;
                }

                if (_window != null) _window.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
