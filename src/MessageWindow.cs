using System;
using System.Drawing;
using System.Windows.Forms;

namespace AutoTyper
{
    /// <summary>
    /// An invisible form that owns the global hotkey registrations and serves as the
    /// marshalling target for background-thread callbacks. It is never shown, so it has no
    /// taskbar button and no Alt+Tab entry.
    /// </summary>
    internal sealed class MessageWindow : Form
    {
        public event Action<int> HotkeyPressed;

        public MessageWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
            Opacity = 0;

            // Force handle creation without ever making the window visible, so that
            // RegisterHotKey has something to target.
            IntPtr unused = Handle;
            GC.KeepAlive(unused);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                Action<int> handler = HotkeyPressed;
                if (handler != null) handler(m.WParam.ToInt32());
                return;
            }

            base.WndProc(ref m);
        }

        /// <summary>Runs <paramref name="action"/> on the UI thread; safe to call from any thread.</summary>
        public void Post(Action action)
        {
            if (action == null) return;

            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (Exception)
            {
                // Racing with shutdown; nothing useful to do.
            }
        }
    }
}
