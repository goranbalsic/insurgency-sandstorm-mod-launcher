using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SandstormModLauncher.ViewModels;

namespace SandstormModLauncher.Views.Pages
{
    public partial class PlayPage : UserControl
    {
        public PlayPage() { InitializeComponent(); }

        /// <summary>
        /// The slider is bound one way so range coercion (while rows are created or scrolled) can never
        /// overwrite a rule. Only real user input is written back.
        /// </summary>
        private void RuleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var slider = (Slider)sender;
            bool user = slider.IsKeyboardFocused || (slider.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed);
            if (!user || !(slider.DataContext is RuleItem rule)) return;
            if (Math.Abs(rule.Number - e.NewValue) > 1e-9) rule.Number = e.NewValue;
        }
    }
}
