using System;

using Avalonia.Controls;
using Avalonia.Media;

namespace AppPortal.Client.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => ApplyBackdrop();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty)
            {
                ApplyBackdrop();
            }
        };
    }

    /// <summary>
    /// Windows 11 draws Mica behind the window when the hint is honoured; the window must then be transparent.
    /// Anywhere else (older Windows, Linux, remote sessions) the solid base color stands in for it.
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
