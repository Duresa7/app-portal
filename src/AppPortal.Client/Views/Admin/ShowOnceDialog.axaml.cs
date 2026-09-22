using System;

using AppPortal.Client.ViewModels.Admin;

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace AppPortal.Client.Views.Admin;

public partial class ShowOnceDialog : UserControl
{
    public ShowOnceDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The clipboard belongs to the window, so the copy happens here and the view model only hears
    /// whether it worked. The secret goes to the clipboard and nowhere else.
    /// </summary>
    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShowOnceViewModel { Secret: { } secret } model)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            model.ReportCopy(false);
            return;
        }

        try
        {
            await clipboard.SetTextAsync(secret);
            model.ReportCopy(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Another program holding the clipboard open is enough to make this throw on Windows. The
            // text box still shows the secret selected, so a copy by hand remains possible.
            model.ReportCopy(false);
        }
    }
}
