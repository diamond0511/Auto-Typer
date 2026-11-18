using System;
using System.Threading;
using System.Windows.Forms;

namespace AutoTyper
{
    internal static class Program
    {
        // Per-user, so one instance per logged-on session rather than one per machine.
        const string MutexName = @"Local\AutoTyper.SingleInstance.6F2A1C84";

        [STAThread]
        static void Main()
        {
            bool isFirstInstance;

            using (Mutex instanceLock = new Mutex(true, MutexName, out isFirstInstance))
            {
                if (!isFirstInstance)
                {
                    MessageBox.Show(
                        "Auto Typer is already running. Look for its icon in the notification area.",
                        "Auto Typer",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Without a main window there is nowhere for a crash to surface, so report it.
                Application.ThreadException += OnThreadException;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                try
                {
                    Application.Run(new TrayApp());
                }
                finally
                {
                    try { instanceLock.ReleaseMutex(); }
                    catch (ApplicationException) { }
                }
            }
        }

        static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Report(e.Exception);
        }

        static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Report(e.ExceptionObject as Exception);
        }

        static void Report(Exception ex)
        {
            string detail = (ex != null) ? ex.ToString() : "Unknown error.";

            MessageBox.Show(
                "Auto Typer hit an unexpected error:\r\n\r\n" + detail,
                "Auto Typer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
