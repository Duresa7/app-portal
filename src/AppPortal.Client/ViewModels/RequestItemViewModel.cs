using System;

using AppPortal.Shared;

using Avalonia.Media;

namespace AppPortal.Client.ViewModels;

/// <summary>One row under Requests: what was asked for, where it stands, and the reason if it is decided.</summary>
public sealed class RequestItemViewModel(AppRequest request)
{
    public AppRequest Request { get; } = request;

    public string Text => Request.Text;
    public DateTimeOffset CreatedAt => Request.CreatedAt;
    public string Reason => Request.Reason ?? "";
    public bool HasReason => !string.IsNullOrWhiteSpace(Request.Reason);

    public string StatusText => Request.Status switch
    {
        AppRequestStatus.Pending => "Pending",
        AppRequestStatus.Approved => "Approved",
        AppRequestStatus.Denied => "Denied",
        _ => Request.Status.ToString(),
    };

    public IBrush StatusBrush => Request.Status switch
    {
        AppRequestStatus.Approved => Brush("SystemFillColorSuccessBrush", "#0F7B0F"),
        AppRequestStatus.Denied => Brush("SystemFillColorCriticalBrush", "#C42B1C"),
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
