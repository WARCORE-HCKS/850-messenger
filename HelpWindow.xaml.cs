using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace Area850;

public partial class HelpWindow : Window
{
    public HelpWindow() => InitializeComponent();

    private void Site_Click(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
