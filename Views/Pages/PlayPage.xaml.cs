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
        /// Narrow page (small window, large text size): the "Your match" column goes (the bottom bar shows the same
        /// summary and the launch button), and the page title makes room for the tabs.
        /// </summary>
        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = root.ActualWidth;
            bool narrow = w < 1020;
            summary.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            gapCol.Width = new GridLength(narrow ? 0 : 20);
            summaryCol.MinWidth = narrow ? 0 : 330;
            summaryCol.Width = narrow ? new GridLength(0) : new GridLength(0.6, GridUnitType.Star);
            summaryThumb.Height = w < 1180 ? 84 : 130;

            titleBox.Visibility = Visibility.Visible;
            titleBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tabsBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (titleBox.DesiredSize.Width + tabsBox.DesiredSize.Width + 16 > w) titleBox.Visibility = Visibility.Collapsed;
        }

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
