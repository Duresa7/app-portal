using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AppPortal.Client;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Last resort. The handlers above this one should catch everything; if one does not, write the
        // detail somewhere a support request can quote instead of the window vanishing silently.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Unobserved task", e.Exception);
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void Log(string kind, Exception? ex)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPortal");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "client.log"),
                $"{DateTimeOffset.Now:u} {kind}: {ex}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing useful is left to do if even logging fails.
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
