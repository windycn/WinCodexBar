using CodexBarWin.Tray;
using System;
using CodexBarWin.Services;
using System.Windows.Forms;

namespace CodexBarWin
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => AppLogService.LogException(e.Exception, "UI thread exception");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception exception)
                {
                    AppLogService.LogException(exception, "AppDomain unhandled exception");
                }
            };
            Application.Run(new CodexBarTrayContext());
        }
    }
}
