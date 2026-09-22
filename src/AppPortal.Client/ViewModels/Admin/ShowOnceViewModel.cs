using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>
/// A secret the server hands out once: a new enrollment key, a device token, a generated password. It
/// is held here, in memory, from the reply until the administrator closes the dialog, and nowhere
/// else. Nothing writes it to disk or to a log, and no other property of the page repeats it, so
/// closing the dialog is the end of it on this PC.
/// </summary>
public sealed partial class ShowOnceViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private string? _secret;

    [ObservableProperty] private string _heading = "";

    [ObservableProperty] private string _explanation = "";

    /// <summary>"Copied.", or how to copy by hand when the clipboard would not take it.</summary>
    [ObservableProperty] private string? _copyStatus;

    [ObservableProperty] private bool _copied;

    public bool IsOpen => Secret is not null;

    public void Show(string heading, string explanation, string secret)
    {
        Heading = heading;
        Explanation = explanation;
        CopyStatus = null;
        Copied = false;
        Secret = secret;
    }

    /// <summary>Called by the dialog after it has tried the clipboard, which only a view can reach.</summary>
    public void ReportCopy(bool copied)
    {
        Copied = copied;
        CopyStatus = copied ? "Copied." : "The clipboard is not available. Select the text and press Ctrl+C.";
    }

    [RelayCommand]
    private void Dismiss()
    {
        Secret = null;
        CopyStatus = null;
        Copied = false;
    }
}
