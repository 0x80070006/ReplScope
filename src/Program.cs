namespace ReplScope;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\ReplScope.SingleInstance", out bool first);
        if (!first) return;
        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void Fatal(Exception? ex) =>
        MessageBox.Show(ex?.Message ?? "Erreur inconnue", "ReplScope", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
