using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AppPortal.Client.Services;
using AppPortal.Client.ViewModels;
using AppPortal.Client.Views;

namespace AppPortal.Client;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = ClientSettings.Load();
            var demo = (desktop.Args ?? []).Contains("--demo");
            IPortalApiClient? api = demo
                ? new DemoPortalApiClient()
                : settings.IsConfigured ? new PortalApiClient(settings) : null;
            var viewModel = new MainViewModel(api, settings, demo);
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            if (api is not null)
            {
                _ = viewModel.RefreshAsync();
            }

            // `--screenshot <file.png> [section]` renders the window once and exits. Used for documentation and UI checks.
            var args = desktop.Args ?? [];
            var theme = Array.IndexOf(args, "--theme");
            if (theme >= 0 && theme + 1 < args.Length)
            {
                RequestedThemeVariant = args[theme + 1].Equals("dark", StringComparison.OrdinalIgnoreCase)
                    ? Avalonia.Styling.ThemeVariant.Dark
                    : Avalonia.Styling.ThemeVariant.Light;
            }

            var index = Array.IndexOf(args, "--screenshot");
            if (index >= 0 && index + 1 < args.Length)
            {
                var section = index + 2 < args.Length && int.TryParse(args[index + 2], out var s) ? s : 0;
                _ = CaptureAsync(window, viewModel, args[index + 1], section, desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task CaptureAsync(Window window, MainViewModel viewModel, string path, int section, IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        viewModel.SelectedSection = section;
        await Task.Delay(500);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(path);
        });
        desktop.Shutdown();
    }
}
