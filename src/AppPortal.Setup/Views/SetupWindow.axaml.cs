using System.Threading.Tasks;

using AppPortal.Setup.ViewModels;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;

namespace AppPortal.Setup.Views;

public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            ApplyBackdrop();
            if (DataContext is SetupViewModel model)
            {
                model.CopyToClipboard = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
            }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty)
            {
                ApplyBackdrop();
            }
        };

        // A tech doing a row of machines never touches the mouse. Enter is the primary button through
        // IsDefault; Escape is handled here because the wizard, not the window, decides when it applies.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is SetupViewModel model)
            {
                model.CancelCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>
    /// Windows 11 draws Mica behind the window when the hint is honoured; the window must then be
    /// transparent. Anywhere else the solid base color stands in for it, exactly as in the client.
    /// </summary>
    private void ApplyBackdrop()
    {
        if (ActualTransparencyLevel == WindowTransparencyLevel.Mica)
        {
            Background = Brushes.Transparent;
        }
        else if (this.TryFindResource("SolidBackgroundFillColorBaseBrush", ActualThemeVariant, out var brush) && brush is IBrush solid)
        {
            Background = solid;
        }
    }
}
