using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace AutoTyper
{
    internal sealed class SettingsForm : Form
    {
        readonly AppSettings _working;

        HotkeyBox _hkType;
        HotkeyBox _hkAbort;
        HotkeyBox _hkSettings;
        HotkeyBox _hkQuit;

        NumericUpDown _startDelay;
        NumericUpDown _minDelay;
        NumericUpDown _maxDelay;
        Label _speedInfo;

        CheckBox _useRealKeys;
        CheckBox _humanRhythm;
        CheckBox _makeMistakes;
        CheckBox _newlineAsEnter;
        CheckBox _tabAsTab;
        CheckBox _stripTrailing;
        CheckBox _stopOnFocusChange;
        CheckBox _stopOnMouseClick;
        CheckBox _showTray;
        CheckBox _showNotifications;

        /// <summary>Populated when the dialog is accepted.</summary>
        public AppSettings Result
        {
            get { return _working; }
        }

        public SettingsForm(AppSettings current)
        {
            _working = current.Clone();
            BuildUi();
            LoadFrom(_working);
            UpdateSpeedInfo();
        }

        void BuildUi()
        {
            Text = "Auto Typer - Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(448, 648);

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // Falls back to the default form icon.
            }

            ToolTip tips = new ToolTip();
            tips.AutoPopDelay = 12000;
            tips.InitialDelay = 400;

            // ---- hotkeys ----
            GroupBox gHotkeys = new GroupBox();
            gHotkeys.Text = "Hotkeys";
            gHotkeys.Location = new Point(12, 10);
            gHotkeys.Size = new Size(424, 150);
            Controls.Add(gHotkeys);

            _hkType = AddHotkeyRow(gHotkeys, "Type clipboard", 26);
            _hkAbort = AddHotkeyRow(gHotkeys, "Stop typing", 56);
            _hkSettings = AddHotkeyRow(gHotkeys, "Open settings", 86);
            _hkQuit = AddHotkeyRow(gHotkeys, "Quit Auto Typer", 116);

            tips.SetToolTip(_hkType, "Click the box and press the combination you want. Backspace clears it.");
            tips.SetToolTip(_hkQuit, "Keep this set - it is how you exit when the tray icon is hidden.");

            // ---- speed ----
            GroupBox gSpeed = new GroupBox();
            gSpeed.Text = "Speed";
            gSpeed.Location = new Point(12, 170);
            gSpeed.Size = new Size(424, 122);
            Controls.Add(gSpeed);

            Label lStart = new Label();
            lStart.Text = "Wait before typing starts";
            lStart.Location = new Point(14, 30);
            lStart.Size = new Size(190, 20);
            gSpeed.Controls.Add(lStart);

            _startDelay = new NumericUpDown();
            _startDelay.Location = new Point(210, 27);
            _startDelay.Size = new Size(80, 24);
            _startDelay.Minimum = 0;
            _startDelay.Maximum = 60000;
            _startDelay.Increment = 100;
            gSpeed.Controls.Add(_startDelay);

            Label lStartMs = new Label();
            lStartMs.Text = "ms";
            lStartMs.Location = new Point(296, 30);
            lStartMs.Size = new Size(30, 20);
            gSpeed.Controls.Add(lStartMs);

            tips.SetToolTip(_startDelay, "Time to click into the target field after pressing the hotkey.");

            Label lKey = new Label();
            lKey.Text = "Delay between keys";
            lKey.Location = new Point(14, 62);
            lKey.Size = new Size(190, 20);
            gSpeed.Controls.Add(lKey);

            _minDelay = new NumericUpDown();
            _minDelay.Location = new Point(210, 59);
            _minDelay.Size = new Size(70, 24);
            _minDelay.Minimum = 0;
            _minDelay.Maximum = 5000;
            _minDelay.ValueChanged += OnDelayChanged;
            gSpeed.Controls.Add(_minDelay);

            Label lTo = new Label();
            lTo.Text = "to";
            lTo.Location = new Point(286, 62);
            lTo.Size = new Size(20, 20);
            gSpeed.Controls.Add(lTo);

            _maxDelay = new NumericUpDown();
            _maxDelay.Location = new Point(308, 59);
            _maxDelay.Size = new Size(70, 24);
            _maxDelay.Minimum = 0;
            _maxDelay.Maximum = 5000;
            _maxDelay.ValueChanged += OnDelayChanged;
            gSpeed.Controls.Add(_maxDelay);

            Label lKeyMs = new Label();
            lKeyMs.Text = "ms";
            lKeyMs.Location = new Point(384, 62);
            lKeyMs.Size = new Size(30, 20);
            gSpeed.Controls.Add(lKeyMs);

            tips.SetToolTip(_minDelay, "With the measured rhythm on, the middle of this range is the typical gap between keys and the recorded distribution is scaled to match it. The default, 80 to 120 ms, is the speed the recordings were made at.");
            tips.SetToolTip(_maxDelay, "Set both to 0 for maximum speed. Some remote sessions and web fields lose characters when typing too fast.");

            _speedInfo = new Label();
            _speedInfo.Location = new Point(16, 94);
            _speedInfo.Size = new Size(396, 20);
            _speedInfo.ForeColor = SystemColors.GrayText;
            gSpeed.Controls.Add(_speedInfo);

            // ---- behaviour ----
            GroupBox gBehaviour = new GroupBox();
            gBehaviour.Text = "Behaviour";
            gBehaviour.Location = new Point(12, 302);
            gBehaviour.Size = new Size(424, 292);
            Controls.Add(gBehaviour);

            _useRealKeys = AddCheck(gBehaviour, "Type with real keys (press Shift for capitals)", 26);
            _humanRhythm = AddCheck(gBehaviour, "Use the measured typing rhythm", 52);
            _humanRhythm.CheckedChanged += OnDelayChanged;   // changes the speed estimate
            _makeMistakes = AddCheck(gBehaviour, "Make and correct occasional mistakes", 78);
            _newlineAsEnter = AddCheck(gBehaviour, "Press Enter for line breaks", 104);
            _tabAsTab = AddCheck(gBehaviour, "Press Tab for tab characters", 130);
            _stripTrailing = AddCheck(gBehaviour, "Ignore a line break at the very end", 156);
            _stopOnFocusChange = AddCheck(gBehaviour, "Stop if the target window loses focus", 182);
            _stopOnMouseClick = AddCheck(gBehaviour, "Stop if the mouse is clicked", 208);
            _showTray = AddCheck(gBehaviour, "Show the tray icon", 234);
            _showNotifications = AddCheck(gBehaviour, "Show notifications", 260);

            tips.SetToolTip(_useRealKeys, "On: each character is sent as the key that actually types it, with Shift held down for capitals - what a web form expects. Off: characters are sent as Unicode packets, which carry no physical key. Turn off only if a program refuses the real keys.");
            tips.SetToolTip(_humanRhythm, "On: the timing comes from recordings of real typists - keys held about 120 ms and overlapping, a beat on the first letter after a space, longer before a capital and after punctuation. Off: a flat random delay inside the range above.");
            tips.SetToolTip(_makeMistakes, "Types a wrong key about once every 46 characters, carries on for a character or two, then backspaces over it, at the rate and speed real typists did. A correction always finishes even if you press the stop hotkey part way through, so no typo is left behind.");

            tips.SetToolTip(_newlineAsEnter, "Off: line breaks are skipped instead, which suits single-line fields.");
            tips.SetToolTip(_tabAsTab, "Off: tabs are sent as plain characters rather than moving to the next field.");
            tips.SetToolTip(_stripTrailing, "Copied text often ends in a line break, which would submit the form you are filling in.");
            tips.SetToolTip(_stopOnFocusChange, "A safety net: if you click away mid-run, the rest of the text is not typed into the wrong window.");
            tips.SetToolTip(_stopOnMouseClick, "The mouse cannot be used while Auto Typer is typing, so clicking anything stops the run and gives you the machine back. Any of the five buttons counts. Moving the mouse does not, so the pointer can be nudged without losing the run, and clicking into the target field during the wait above does not count either.");
            tips.SetToolTip(_showTray, "Off: Auto Typer runs with no visible trace. Use the quit hotkey to exit and the settings hotkey to get back here.");
            tips.SetToolTip(_showNotifications, "Off: no balloon tips when typing starts or finishes.");

            // ---- buttons ----
            Button reset = new Button();
            reset.Text = "Reset defaults";
            reset.Location = new Point(12, 606);
            reset.Size = new Size(110, 28);
            reset.Click += OnReset;
            Controls.Add(reset);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.Location = new Point(248, 606);
            cancel.Size = new Size(88, 28);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            Button save = new Button();
            save.Text = "Save";
            save.Location = new Point(344, 606);
            save.Size = new Size(92, 28);
            save.Click += OnSave;
            Controls.Add(save);

            AcceptButton = save;
            CancelButton = cancel;
        }

        static HotkeyBox AddHotkeyRow(GroupBox parent, string label, int y)
        {
            Label l = new Label();
            l.Text = label;
            l.Location = new Point(14, y + 4);
            l.Size = new Size(140, 20);
            parent.Controls.Add(l);

            HotkeyBox box = new HotkeyBox();
            box.Location = new Point(160, y);
            box.Size = new Size(190, 24);
            parent.Controls.Add(box);

            return box;
        }

        static CheckBox AddCheck(GroupBox parent, string text, int y)
        {
            CheckBox cb = new CheckBox();
            cb.Text = text;
            cb.Location = new Point(14, y);
            cb.Size = new Size(396, 22);
            parent.Controls.Add(cb);
            return cb;
        }

        void LoadFrom(AppSettings s)
        {
            _hkType.Value = s.TypeHotkey;
            _hkAbort.Value = s.AbortHotkey;
            _hkSettings.Value = s.SettingsHotkey;
            _hkQuit.Value = s.QuitHotkey;

            _startDelay.Value = Clamp(s.StartDelayMs, _startDelay);
            _minDelay.Value = Clamp(s.MinKeyDelayMs, _minDelay);
            _maxDelay.Value = Clamp(s.MaxKeyDelayMs, _maxDelay);

            _useRealKeys.Checked = s.UseRealKeys;
            _humanRhythm.Checked = s.HumanRhythm;
            _makeMistakes.Checked = s.MakeMistakes;
            _newlineAsEnter.Checked = s.NewlineAsEnter;
            _tabAsTab.Checked = s.TabAsTab;
            _stripTrailing.Checked = s.StripTrailingNewline;
            _stopOnFocusChange.Checked = s.StopOnFocusChange;
            _stopOnMouseClick.Checked = s.StopOnMouseClick;
            _showTray.Checked = s.ShowTrayIcon;
            _showNotifications.Checked = s.ShowNotifications;
        }

        static decimal Clamp(int value, NumericUpDown box)
        {
            if (value < box.Minimum) return box.Minimum;
            if (value > box.Maximum) return box.Maximum;
            return value;
        }

        void OnDelayChanged(object sender, EventArgs e)
        {
            if (_maxDelay.Value < _minDelay.Value)
            {
                // Keep the range coherent while the user is editing either end.
                if (sender == _minDelay) _maxDelay.Value = _minDelay.Value;
                else _minDelay.Value = _maxDelay.Value;
            }

            UpdateSpeedInfo();
        }

        void UpdateSpeedInfo()
        {
            // Delay controls are built before the checkboxes, and changing their bounds
            // can raise ValueChanged on the way.
            if (_speedInfo == null || _humanRhythm == null) return;

            double avg = ((double)_minDelay.Value + (double)_maxDelay.Value) / 2.0;

            if (avg <= 0.5)
            {
                _speedInfo.ForeColor = SystemColors.GrayText;
                _speedInfo.Text = "As fast as the target window accepts input.";
                return;
            }

            // With the measured rhythm on, the gaps are not all the midpoint: word
            // boundaries, capitals and punctuation all stretch it, so the average is
            // about a quarter higher and the real throughput correspondingly lower.
            double effective = _humanRhythm.Checked ? avg * RhythmProfile.MeanGapRatio : avg;

            double perSecond = 1000.0 / effective;
            string speed = string.Format(
                CultureInfo.CurrentCulture,
                "About {0:0.0} characters per second, near {1:0} wpm.",
                perSecond,
                perSecond * 60.0 / 5.0);

            if (avg < AppSettings.SafeKeyDelayMs)
            {
                // Measured against Windows 11 Notepad: below ~60 ms it substitutes characters.
                _speedInfo.ForeColor = Color.Firebrick;
                _speedInfo.Text = speed + "  Below " + AppSettings.SafeKeyDelayMs + " ms, some apps garble text.";
            }
            else
            {
                _speedInfo.ForeColor = SystemColors.GrayText;
                _speedInfo.Text = speed;
            }
        }

        void OnReset(object sender, EventArgs e)
        {
            DialogResult confirm = MessageBox.Show(this,
                "Reset every setting to its default?",
                "Auto Typer",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm == DialogResult.Yes) LoadFrom(new AppSettings());
        }

        void OnSave(object sender, EventArgs e)
        {
            if (!_hkType.Value.IsValid)
            {
                Warn("Set a hotkey for \"Type clipboard\" - without it there is no way to start typing.");
                _hkType.Focus();
                return;
            }

            if (!_hkQuit.Value.IsValid && !_showTray.Checked)
            {
                Warn("With the tray icon hidden, the quit hotkey is the only way to exit Auto Typer.\r\n\r\nSet a quit hotkey, or keep the tray icon visible.");
                _hkQuit.Focus();
                return;
            }

            if (!_hkSettings.Value.IsValid && !_showTray.Checked)
            {
                Warn("With the tray icon hidden, the settings hotkey is the only way to reopen this window.\r\n\r\nSet a settings hotkey, or keep the tray icon visible.");
                _hkSettings.Focus();
                return;
            }

            string typable = FindTypable();
            if (typable != null)
            {
                // Shift is the one modifier Auto Typer holds down itself.
                Warn("The hotkey " + typable + " would be fired by Auto Typer's own typing.\r\n\r\n" +
                     "Capitals are typed by genuinely holding Shift, so this combination goes off " +
                     "as soon as that character appears in your text, and the run stops, restarts " +
                     "or quits part way through.\r\n\r\n" +
                     "Add Ctrl, Alt or Win to it, or pick a different key.");
                return;
            }

            string duplicate = FindDuplicate();
            if (duplicate != null)
            {
                Warn("The hotkey " + duplicate + " is assigned to more than one action.");
                return;
            }

            string risky = FindRisky();
            if (risky != null)
            {
                // A global hotkey wins over the foreground app, so binding a common editing
                // shortcut takes it away from every program on the machine.
                DialogResult go = MessageBox.Show(this,
                    risky + " is a standard shortcut in most programs.\r\n\r\n" +
                    "A global hotkey takes priority, so it will stop working everywhere else " +
                    "while Auto Typer is running.\r\n\r\nUse it anyway?",
                    "Auto Typer",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (go != DialogResult.Yes) return;
            }

            _working.TypeHotkey = _hkType.Value;
            _working.AbortHotkey = _hkAbort.Value;
            _working.SettingsHotkey = _hkSettings.Value;
            _working.QuitHotkey = _hkQuit.Value;

            _working.StartDelayMs = (int)_startDelay.Value;
            _working.MinKeyDelayMs = (int)_minDelay.Value;
            _working.MaxKeyDelayMs = (int)_maxDelay.Value;

            _working.UseRealKeys = _useRealKeys.Checked;
            _working.HumanRhythm = _humanRhythm.Checked;
            _working.MakeMistakes = _makeMistakes.Checked;
            _working.NewlineAsEnter = _newlineAsEnter.Checked;
            _working.TabAsTab = _tabAsTab.Checked;
            _working.StripTrailingNewline = _stripTrailing.Checked;
            _working.StopOnFocusChange = _stopOnFocusChange.Checked;
            _working.StopOnMouseClick = _stopOnMouseClick.Checked;
            _working.ShowTrayIcon = _showTray.Checked;
            _working.ShowNotifications = _showNotifications.Checked;

            _working.Normalize();

            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// Flags Ctrl-only bindings that collide with near-universal editing shortcuts.
        /// Returns the first offender, or null.
        /// </summary>
        string FindRisky()
        {
            Keys[] common =
            {
                Keys.C, Keys.V, Keys.X, Keys.Z, Keys.Y, Keys.A, Keys.S, Keys.W,
                Keys.T, Keys.N, Keys.O, Keys.P, Keys.F, Keys.D, Keys.R, Keys.B, Keys.I, Keys.U
            };

            Hotkey[] keys = { _hkType.Value, _hkAbort.Value, _hkSettings.Value, _hkQuit.Value };

            for (int i = 0; i < keys.Length; i++)
            {
                Hotkey hk = keys[i];
                if (!hk.IsValid) continue;

                // Only bare Ctrl+<letter> is risky; adding Alt or Shift makes it unambiguous.
                if (!hk.Ctrl || hk.Alt || hk.Shift || hk.Win) continue;

                for (int j = 0; j < common.Length; j++)
                {
                    if (hk.Key == common[j]) return hk.ToString();
                }
            }

            return null;
        }

        /// <summary>
        /// Finds a hotkey the engine would trigger by typing, which is any whose only
        /// modifier is Shift. Returns the first offender, or null.
        /// </summary>
        string FindTypable()
        {
            Hotkey[] keys = { _hkType.Value, _hkAbort.Value, _hkSettings.Value, _hkQuit.Value };

            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i].TriggeredByTyping) return keys[i].ToString();
            }

            return null;
        }

        string FindDuplicate()
        {
            Hotkey[] keys = { _hkType.Value, _hkAbort.Value, _hkSettings.Value, _hkQuit.Value };

            for (int i = 0; i < keys.Length; i++)
            {
                if (!keys[i].IsValid) continue;

                for (int j = i + 1; j < keys.Length; j++)
                {
                    if (keys[i].SameAs(keys[j])) return keys[i].ToString();
                }
            }

            return null;
        }

        void Warn(string message)
        {
            MessageBox.Show(this, message, "Auto Typer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
