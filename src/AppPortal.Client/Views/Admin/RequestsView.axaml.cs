using Avalonia.Controls;

namespace AppPortal.Client.Views.Admin;

public partial class RequestsView : UserControl
{
    public RequestsView()
    {
        InitializeComponent();
        // Straight into the reason box, so a decision can be made from the keyboard: type, then Enter.
        DecisionPopup.Opened += (_, _) => ReasonBox.Focus();
    }
}
