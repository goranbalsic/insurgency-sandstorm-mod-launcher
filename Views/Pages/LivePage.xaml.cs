using System.Windows.Controls;
using System.Windows.Input;
using SandstormModLauncher.ViewModels;

namespace SandstormModLauncher.Views.Pages
{
    public partial class LivePage : UserControl
    {
        public LivePage() { InitializeComponent(); }

        private void CommandBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || !(DataContext is MainViewModel vm)) return;
            if (vm.SendCustomCommand.CanExecute(null)) vm.SendCustomCommand.Execute(null);
            e.Handled = true;
        }
    }
}
