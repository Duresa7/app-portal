using System;
using Avalonia.Media;
using AppPortal.Shared;

namespace AppPortal.Client.ViewModels;

/// <summary>One row in Activity: the install request plus the color that stands for its state.</summary>
public sealed class ActivityItemViewModel(InstallRequest request)
{
    public InstallRequest Request { get; } = request;

    public string AppName => Request.AppName;
    public string Detail => Request.Detail ?? "";
    public DateTimeOffset RequestedAt => Request.RequestedAt;

    public string StateText => Request.State switch
    {
        InstallState.Queued => "Queued",
        InstallState.Running => $"Installing {Request.PercentComplete}%",
        InstallState.Succeeded => "Installed",
        InstallState.Failed => "Failed",
        InstallState.Cancelled => "Cancelled",
        _ => Request.State.ToString(),
    };

    public IBrush StateBrush => Request.State switch
    {
        InstallState.Succeeded => Brush("SystemFillColorSuccessBrush", "#0F7B0F"),
        InstallState.Failed => Brush("SystemFillColorCriticalBrush", "#C42B1C"),
        InstallState.Cancelled => Brush("TextFillColorTertiaryBrush", "#72000000"),
        _ => Brush("AccentFillColorDefaultBrush", "#005FB8"),
    };

    private static IBrush Brush(string key, string fallback)
    {
        var app = Avalonia.Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallback));
    }
}
