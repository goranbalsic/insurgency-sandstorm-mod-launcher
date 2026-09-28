using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using SandstormModLauncher.Core;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.Views
{
    /// <summary>
    /// Translates the texts written in the XAML pages when they appear (text, button captions, tooltips, the grey hint in
    /// empty text boxes, menu entries), and again when the language changes. Texts that come from the view model are
    /// translated there. The English original is kept on each element, so switching back or to another language works.
    /// </summary>
    public static class LocHook
    {
        private sealed class Record
        {
            public string English;
            public string Shown;
        }

        private static readonly DependencyProperty RecordsProperty =
            DependencyProperty.RegisterAttached("LocRecords", typeof(Dictionary<DependencyProperty, Record>), typeof(LocHook));
        private static bool used;

        public static void Install()
        {
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) => Apply(s as DependencyObject)), true);
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, e) => Apply(s as DependencyObject)), true);
            Loc.Changed += () =>
            {
                used = true;
                foreach (Window w in Application.Current.Windows) ApplyTree(w);
            };
        }

        /// <summary>Every element under a root (the visual tree, and the text inside text blocks).</summary>
        public static void ApplyTree(DependencyObject root)
        {
            if (root == null) return;
            Apply(root);
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++) ApplyTree(VisualTreeHelper.GetChild(root, i));
        }

        private static void Apply(DependencyObject d)
        {
            // Until a language other than English is picked, nothing is touched.
            if (d == null || (!used && Loc.IsEnglish)) return;
            switch (d)
            {
                case TextBlock tb when tb.TemplatedParent is ContentPresenter cp && cp.Content is string && cp.ContentTemplate == null && cp.ContentTemplateSelector == null:
                    break;   // made by a content presenter for a plain string: its owner (a button, a label) is translated
                case TextBlock tb:
                    // Text in pieces ("Key delay · " + a value + " ms"): each piece on its own.
                    if (tb.Inlines.Count > 1 || tb.Inlines.FirstInline is Span) foreach (var inline in new List<Inline>(tb.Inlines)) ApplyInline(inline);
                    else if (tb.ReadLocalValue(TextBlock.TextProperty) != DependencyProperty.UnsetValue || HasRecord(tb, TextBlock.TextProperty)) Translate(tb, TextBlock.TextProperty);
                    else if (IsLiteral(tb, TextBlock.TextProperty)) Translate(tb, TextBlock.TextProperty);
                    else foreach (var inline in new List<Inline>(tb.Inlines)) ApplyInline(inline);   // a copy: changing a run changes the collection
                    break;
                case HeaderedContentControl hc:
                    if (hc.Header is string) Translate(hc, HeaderedContentControl.HeaderProperty);
                    if (hc.Content is string) Translate(hc, ContentControl.ContentProperty);
                    break;
                case HeaderedItemsControl hi:
                    if (hi.Header is string) Translate(hi, HeaderedItemsControl.HeaderProperty);
                    break;
                case Controls.Stepper st:
                    Translate(st, Controls.Stepper.LabelProperty);
                    Translate(st, Controls.Stepper.HintProperty);
                    break;
                case ContentControl cc:
                    if (cc.Content is string) Translate(cc, ContentControl.ContentProperty);
                    break;
            }
            if (d is FrameworkElement fe)
            {
                if (fe.ToolTip is string) Translate(fe, FrameworkElement.ToolTipProperty);
                if (fe is TextBox && fe.Tag is string) Translate(fe, FrameworkElement.TagProperty);
            }
        }

        private static void ApplyInline(Inline inline)
        {
            switch (inline)
            {
                case Run run: Translate(run, Run.TextProperty); break;
                case Span span: foreach (var child in new List<Inline>(span.Inlines)) ApplyInline(child); break;
            }
        }

        /// <summary>A text written in the XAML (on the element or in its template): not a binding and not a style's.</summary>
        private static bool IsLiteral(DependencyObject d, DependencyProperty p)
        {
            var src = DependencyPropertyHelper.GetValueSource(d, p);
            return !src.IsExpression && (src.BaseValueSource == BaseValueSource.Local || src.BaseValueSource == BaseValueSource.ParentTemplate);
        }

        private static bool HasRecord(DependencyObject d, DependencyProperty p) =>
            d.GetValue(RecordsProperty) is Dictionary<DependencyProperty, Record> m && m.ContainsKey(p);

        private static void Translate(DependencyObject d, DependencyProperty p)
        {
            if (!IsLiteral(d, p)) return;
            if (!(d.GetValue(p) is string now)) return;
            var records = d.GetValue(RecordsProperty) as Dictionary<DependencyProperty, Record>;
            if (records == null) { records = new Dictionary<DependencyProperty, Record>(); d.SetValue(RecordsProperty, records); }
            // The English text is kept the first time; a value changed since by the page itself is its new English text.
            if (!records.TryGetValue(p, out var rec)) records[p] = rec = new Record { English = now, Shown = now };
            else if (now != rec.Shown) { rec.English = now; rec.Shown = now; }
            string text = Loc.T(rec.English);
            if (!string.Equals(now, text, StringComparison.Ordinal)) d.SetCurrentValue(p, text);
            rec.Shown = text;
        }
    }

    /// <summary>Changes when the language changes (for bindings that must show a translated text).</summary>
    public sealed class LocSource : INotifyPropertyChanged
    {
        public static LocSource Instance { get; } = new LocSource();
        private int version;
        public int Version => version;
        public event PropertyChangedEventHandler PropertyChanged;

        private LocSource()
        {
            Loc.Changed += () => { version++; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version))); };
        }
    }

    /// <summary>Translates its parameter (the English text); the bound value is only there to update on a language change.</summary>
    public sealed class TrConverter : IValueConverter
    {
        public static TrConverter Instance { get; } = new TrConverter();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Loc.T(parameter as string);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>
    /// {v:Tr 'English text'}: a translated text where the automatic translation cannot reach, such as the values of a
    /// style's setters and triggers.
    /// </summary>
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class Tr : MarkupExtension
    {
        public string Text { get; set; }
        public Tr() { }
        public Tr(string text) { Text = text; }

        public override object ProvideValue(IServiceProvider serviceProvider) =>
            new Binding(nameof(LocSource.Version)) { Source = LocSource.Instance, Converter = TrConverter.Instance, ConverterParameter = Text, Mode = BindingMode.OneWay };
    }
}
