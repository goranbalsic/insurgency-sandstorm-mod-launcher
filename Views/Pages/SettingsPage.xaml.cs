using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SandstormModLauncher.Views.Pages
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage() { InitializeComponent(); }

        // The report preview sits low on the page: scroll to it when it opens.
        private void SharePanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool shown && shown)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new System.Action(() => sharePanel.BringIntoView()));
        }
    }
}