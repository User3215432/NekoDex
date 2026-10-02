using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MangaLibraryApp;

public partial class App : Application
{
    private const int AttachParentProcess = -1;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MangaLibraryApp");

    public static string LogFilePath { get; } = Path.Combine(LogDirectory, "startup.log");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            AttachConsole(AttachParentProcess);
        }
        catch
        {
            // Konsole ist optional (Start per Doppelklick)
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFatal("Unbehandelte Ausnahme (AppDomain)", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("Unbeobachtete Task-Ausnahme: " + Flatten(args.Exception));
            args.SetObserved();
        };

        try
        {
            Log("Start " + DateTime.Now.ToString("O") + "  " + typeof(App).Assembly.Location);
            Directory.CreateDirectory(LogDirectory);
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            ReportFatal("Die Anwendung konnte nicht gestartet werden.", ex);
            Shutdown(-1);
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportFatal("Unbehandelte UI-Ausnahme", e.Exception);
        e.Handled = true;
        if (e.Exception is System.Windows.Markup.XamlParseException)
            Shutdown(-1);
    }

    public static void Log(string message)
    {
        var line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message;
        try { Console.WriteLine(line); }
        catch { /* keine Konsole */ }
        try { Trace.WriteLine(line); }
        catch { /* Trace optional */ }
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
        catch
        {
            // Logging darf den Start nicht verhindern
        }
    }

    public static void ReportFatal(string title, Exception? ex)
    {
        var text = Flatten(ex);
        Log(title + ": " + text);
        try
        {
            MessageBox.Show(
                title + "\n\n" + text + "\n\nProtokoll: " + LogFilePath,
                "Manga Library",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // MessageBox kann vor dem Dispatcher scheitern
        }
    }

    public static string Flatten(Exception? ex)
    {
        if (ex is null)
            return "(keine Details)";
        var sb = new StringBuilder();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (sb.Length > 0)
                sb.Append(" → ");
            sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
        }

        return sb.ToString();
    }
}
