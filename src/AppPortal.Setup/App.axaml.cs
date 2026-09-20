using System.Net.Http;

using AppPortal.Setup.Services;
using AppPortal.Setup.ViewModels;
using AppPortal.Setup.Views;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AppPortal.Setup;

public partial class App : Application
{
    /// <summary>
    /// What the process returns after the window closes. The wizard is run from scripts as well as from
    /// a double-click, so it answers with the same codes <c>/quiet</c> does.
    /// </summary>
    public static int ExitCode { get; set; }

    /// <summary>The command line, handed over by <see cref="Program"/> before Avalonia starts.</summary>
    public static SetupArguments? Arguments { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var probe = new HttpEnrollmentProbe(new HttpClient { Timeout = HttpEnrollmentProbe.Timeout });
            var viewModel = new SetupViewModel(Composition.Run(probe), probe, Arguments)
            {
                Close = () => desktop.MainWindow?.Close(),
                Launch = Composition.Launch,
            };

            // The window has no say in the exit code, and the view model has no way to reach the process.
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SetupViewModel.ExitCode))
                {
                    ExitCode = viewModel.ExitCode;
                }
            };

            desktop.MainWindow = new SetupWindow { DataContext = viewModel };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
