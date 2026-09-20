using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;

using AppPortal.Setup.Services;

using Avalonia;

namespace AppPortal.Setup;

internal sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var parsed = SetupArguments.Parse(args);
        if (parsed.Arguments is not { } arguments)
        {
            Say(parsed.Error + Environment.NewLine + Environment.NewLine + SetupArguments.Usage);
            return ExitCodes.InvalidArguments;
        }

        if (arguments.Help)
        {
            Say(SetupArguments.Usage);
            return ExitCodes.Success;
        }

        if (arguments.Quiet)
        {
            var probe = new HttpEnrollmentProbe(new HttpClient { Timeout = HttpEnrollmentProbe.Timeout });
            return SilentRun
                .ExecuteAsync(Composition.Run(probe), arguments, Say, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        App.Arguments = arguments;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return App.ExitCode;
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Writes a line where a command prompt can see it. This is a windowed executable, so it owns no
    /// console; without borrowing the parent's, <c>/quiet</c> would answer with an exit code and silence.
    /// </summary>
    private static void Say(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            AttachConsole(AttachParentProcess);
        }

        Console.WriteLine(text);
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
