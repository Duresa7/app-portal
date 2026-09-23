using System;

using AppPortal.Shared;

using Avalonia.Media;

using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels;

/// <summary>
/// One row under Requests: what was asked for, where it stands, the reason if it is decided, and the
/// app that answers it once an administrator has added one.
/// </summary>
/// <param name="app">The catalog card for the linked app, when this PC is offered it.</param>
/// <param name="show">Opens Apps on that card. It never installs anything.</param>
public sealed class RequestItemViewModel(AppRequest request, AppItemViewModel? app = null, Action<AppItemViewModel>? show = null)
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

    /// <summary>
    /// Where the answer is. The server sends a link only while the app is visible, but whether this PC
    /// can install it is something only this PC knows, from its own catalog.
    /// </summary>
    public string CatalogAppText => Request.CatalogAppId is null
        ? ""
        : app is not null
            ? $"Added to the catalog as {app.Name}."
            : $"Added to the catalog as {Request.CatalogAppName ?? Request.CatalogAppId}, but this PC cannot install it.";

    public bool HasCatalogAppText => CatalogAppText.Length > 0;

    public bool CanShowApp => app is not null && show is not null;

    public IRelayCommand ShowAppCommand { get; } = new RelayCommand(() =>
    {
        if (app is not null)
        {
            show?.Invoke(app);
        }
    });

    private static IBrush Brush(string key, string fallback)
    {
        var current = Avalonia.Application.Current;
        if (current is not null && current.TryGetResource(key, current.ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallback));
    }
}
