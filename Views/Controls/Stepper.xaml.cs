using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SandstormModLauncher.Views.Controls
{
    /// <summary>Label + hint with a - [value] + number editor. Highlights the value when it differs from the default.</summary>
    public partial class Stepper : UserControl
    {
        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(Stepper));
        public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(string), typeof(Stepper));
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int), typeof(Stepper),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((Stepper)d).Show()));
        public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(int), typeof(Stepper), new PropertyMetadata(0));
        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(Stepper), new PropertyMetadata(64));
        public static readonly DependencyProperty ChangedProperty = DependencyProperty.Register(nameof(Changed), typeof(bool), typeof(Stepper),
            new PropertyMetadata(false, (d, e) => ((Stepper)d).Show()));

        public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
        public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }
        public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
        public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
        public bool Changed { get => (bool)GetValue(ChangedProperty); set => SetValue(ChangedProperty, value); }

        public Stepper()
        {
            InitializeComponent();
            Loaded += (s, e) => Show();
        }

        private void Show()
        {
            if (box == null) return;
            box.Text = Value.ToString(CultureInfo.InvariantCulture);
            box.Foreground = (Brush)FindResource(Changed ? "Accent" : "Text");
        }

        private void Commit(int v)
        {
            v = Math.Max(Minimum, Math.Min(Maximum, v));
            if (v != Value) Value = v;
            Show();
        }

        private void Minus_Click(object sender, RoutedEventArgs e) => Commit(Value - 1);
        private void Plus_Click(object sender, RoutedEventArgs e) => Commit(Value + 1);

        private void CommitText()
        {
            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) Commit(v);
            else Show();
        }

        private void Box_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitText(); box.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Up) { Commit(Value + 1); e.Handled = true; }
            else if (e.Key == Key.Down) { Commit(Value - 1); e.Handled = true; }
            else if (e.Key == Key.Escape) { Show(); e.Handled = true; }
        }

        private void Box_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitText();

        private void Box_Wheel(object sender, MouseWheelEventArgs e)
        {
            if (!box.IsKeyboardFocusWithin) return;
            Commit(Value + (e.Delta > 0 ? 1 : -1));
            e.Handled = true;
        }
    }
}
