using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SandstormModLauncher.Core;
using SandstormModLauncher.ViewModels;

namespace SandstormModLauncher.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel vm;

        public MainWindow()
        {
            InitializeComponent();
            vm = new MainViewModel();
            try { vm.State.Store.Load(); }
            catch (Exception ex) { AppLog.Error("Settings load failed", ex); }
            ApplySavedSize();
            MainViewModel.ApplyUiScale(vm.UiScale);
            DataContext = vm;
            Loaded += async (s, e) =>
            {
                await vm.InitializeAsync();
                vm.StartUpdateChecks();
            };
            StateChanged += (s, e) => UpdateChrome();
            Closing += OnClosing;
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewMouseWheel += OnPreviewMouseWheel;
            vm.PropertyChanged += OnViewModelChanged;
            UpdateChrome();
        }

        private void ApplySavedSize()
        {
            var s = vm.State.Settings;
            var work = SystemParameters.WorkArea;
            double w = s.WindowWidth >= MinWidth ? s.WindowWidth : Width;
            double h = s.WindowHeight >= MinHeight ? s.WindowHeight : Height;
            Width = Math.Max(MinWidth, Math.Min(w, work.Width));
            Height = Math.Max(Math.Min(MinHeight, work.Height), Math.Min(h, work.Height));
            // On laptop-sized screens the launcher is meant to fill the screen; a window is only kept
            // when the player chose one and it fits comfortably.
            bool fits = Width <= work.Width * 0.92 && Height <= work.Height * 0.92;
            if (s.WindowMaximized || !fits) WindowState = WindowState.Maximized;
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            var s = vm.State.Settings;
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (!bounds.IsEmpty && bounds.Width > 0) { s.WindowWidth = bounds.Width; s.WindowHeight = bounds.Height; }
            s.WindowMaximized = WindowState == WindowState.Maximized;
            vm.Dispose();
        }

        /// <summary>A maximized chrome-less window hangs over the screen edge by the frame size.</summary>
        private void UpdateChrome()
        {
            if (WindowState == WindowState.Maximized)
            {
                var t = SystemParameters.WindowResizeBorderThickness;
                rootBorder.Margin = new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4);
                rootBorder.BorderThickness = new Thickness(0);
                maxButton.Content = "";
                maxButton.ToolTip = "Restore";
            }
            else
            {
                rootBorder.Margin = new Thickness(0);
                rootBorder.BorderThickness = new Thickness(1);
                maxButton.Content = "";
                maxButton.ToolTip = "Maximize";
            }
        }

        private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.DialogOpen) || !vm.DialogOpen) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (vm.DialogHasInput) { dialogInput.Focus(); dialogInput.SelectAll(); }
                else dialogPrimary.Focus();
            }), DispatcherPriority.Input);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl + / Ctrl - / Ctrl 0: interface size.
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                int step = e.Key == Key.OemPlus || e.Key == Key.Add ? 1 : e.Key == Key.OemMinus || e.Key == Key.Subtract ? -1 : e.Key == Key.D0 || e.Key == Key.NumPad0 ? 0 : 2;
                if (step != 2) { vm.StepUiScale(step); e.Handled = true; return; }
            }
            bool overlay = vm.DialogOpen || vm.MapEditorOpen || vm.LaunchOverlayOpen;
            if (e.Key == Key.F5 && overlay) { e.Handled = true; return; }
            if (vm.DialogOpen)
            {
                if (e.Key == Key.Escape) { vm.DialogCommand.Execute(null); e.Handled = true; }
                else if (e.Key == Key.Enter && !(Keyboard.FocusedElement is Button)) { vm.DialogCommand.Execute(vm.DialogPrimary); e.Handled = true; }
                return;
            }
            if (e.Key != Key.Escape) return;
            if (vm.MapEditorOpen) { vm.MapEditorOpen = false; e.Handled = true; }
            else if (vm.LaunchOverlayOpen && vm.CloseLaunchCommand.CanExecute(null)) { vm.CloseLaunchCommand.Execute(null); e.Handled = true; }
        }

        // Ctrl + mouse wheel: interface size.
        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || e.Delta == 0) return;
            vm.StepUiScale(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }

        private void ProfileMenu_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            var menu = button.ContextMenu;
            if (menu == null) return;
            menu.DataContext = DataContext;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void CloseLaunch_Click(object sender, RoutedEventArgs e) => vm.LaunchOverlayOpen = false;
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
