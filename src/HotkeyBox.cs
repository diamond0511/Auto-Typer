using System;
using System.Windows.Forms;

namespace AutoTyper
{
    /// <summary>
    /// A read-only text box that captures a key combination when focused.
    /// Backspace or Delete clears it.
    /// </summary>
    internal sealed class HotkeyBox : TextBox
    {
        Hotkey _value = new Hotkey();

        public event EventHandler ValueChanged;

        public HotkeyBox()
        {
            ReadOnly = true;
            Cursor = Cursors.Hand;
            TabStop = true;
            ShortcutsEnabled = false;
            Text = "(not set)";
        }

        public Hotkey Value
        {
            get { return _value; }
            set
            {
                _value = (value != null) ? value.Clone() : new Hotkey();
                UpdateText();
            }
        }

        void UpdateText()
        {
            Text = _value.IsValid ? _value.ToString() : "(not set)";
            EventHandler h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            BackColor = System.Drawing.SystemColors.Info;

            // The text is a label, not something to select. WinForms re-selects after
            // OnEnter returns, so clear it once focus has settled.
            BeginInvoke((MethodInvoker)delegate
            {
                try { SelectionStart = Text.Length; SelectionLength = 0; }
                catch (Exception) { }
            });
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            BackColor = System.Drawing.SystemColors.Window;
        }

        /// <summary>
        /// Modified combinations reach the dialog as command keys (Alt+X would fire a mnemonic,
        /// Ctrl+S an accept button), so they have to be intercepted before normal routing.
        /// Unmodified Tab/Escape are left alone so the dialog stays navigable.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!Focused) return base.ProcessCmdKey(ref msg, keyData);

            Keys key = keyData & Keys.KeyCode;
            bool hasModifier = (keyData & (Keys.Control | Keys.Alt | Keys.Shift)) != Keys.None
                               || NativeMethods.IsKeyDown(NativeMethods.VK_LWIN)
                               || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);

            if (!hasModifier && (key == Keys.Tab || key == Keys.Escape))
                return base.ProcessCmdKey(ref msg, keyData);

            if (CaptureCombo(keyData)) return true;

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            CaptureCombo(e.KeyData);
        }

        bool CaptureCombo(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;

            // A modifier on its own is not a hotkey; wait for the real key.
            if (key == Keys.ControlKey || key == Keys.Menu || key == Keys.ShiftKey ||
                key == Keys.LWin || key == Keys.RWin || key == Keys.None)
            {
                return true;
            }

            if (key == Keys.Back || key == Keys.Delete)
            {
                _value = new Hotkey();
                UpdateText();
                return true;
            }

            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            bool alt = (keyData & Keys.Alt) == Keys.Alt;
            bool shift = (keyData & Keys.Shift) == Keys.Shift;
            bool win = NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) ||
                       NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);

            if (!ctrl && !alt && !shift && !win)
            {
                // Registering a bare key globally would swallow it everywhere.
                return true;
            }

            _value = new Hotkey(key, ctrl, alt, shift, win);
            UpdateText();
            return true;
        }
    }
}
