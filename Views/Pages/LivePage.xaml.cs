using System.Linq;
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
            if (!(DataContext is MainViewModel vm)) return;
            if (e.Key == Key.Tab && vm.CommandSuggestions.Count > 0)
            {
                string text = vm.CustomCommand ?? "";
                int bar = text.LastIndexOf('|');
                string prefix = bar >= 0 ? text.Substring(0, bar + 1) + " " : "";
                var first = vm.CommandSuggestions.First();
                vm.CustomCommand = prefix + first.Name + (first.Params.Count > 0 ? " " : "");
                commandBox.CaretIndex = commandBox.Text.Length;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (vm.SendCustomCommand.CanExecute(null)) vm.SendCustomCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
