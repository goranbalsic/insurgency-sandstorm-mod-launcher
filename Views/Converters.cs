using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SandstormModLauncher.Game;

namespace SandstormModLauncher.Views
{
    /// <summary>Visible when the value is "truthy": true, non-empty text, non-zero, non-empty list, non-null object.</summary>
    public sealed class VisibleIf : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool truthy;
            switch (value)
            {
                case null: truthy = false; break;
                case bool b: truthy = b; break;
                case string s: truthy = s.Trim().Length > 0; break;
                case int i: truthy = i != 0; break;
                case long l: truthy = l != 0; break;
                case double d: truthy = Math.Abs(d) > 1e-9; break;
                case ICollection c: truthy = c.Count > 0; break;
                default: truthy = true; break;
            }
            if (parameter is string p && value != null) truthy = string.Equals(value.ToString(), p, StringComparison.OrdinalIgnoreCase);
            if (Invert) truthy = !truthy;
            return truthy ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Compares a value with the converter parameter (for radio buttons bound to one string/enum property).</summary>
    public sealed class EqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool b && b ? parameter?.ToString() : Binding.DoNothing;
    }

    /// <summary>Local image file to a cached, frozen bitmap (never loads from the network).</summary>
    public sealed class ImageFromPath : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, ImageSource> cache = new ConcurrentDictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public int DecodeWidth { get; set; } = 480;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string path = value as string;
            if (string.IsNullOrEmpty(path)) return null;
            return cache.GetOrAdd(path + "|" + DecodeWidth, _ => Load(path, DecodeWidth));
        }

        public static ImageSource Load(string path, int width)
        {
            try
            {
                if (!Path.IsPathRooted(path) || !File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = width;
                bmp.StreamSource = new MemoryStream(File.ReadAllBytes(path));
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class StepIcon : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            switch (value as StepState? ?? StepState.Pending)
            {
                case StepState.Done: return "";
                case StepState.Active: return "";
                case StepState.Failed: return "";
                case StepState.Warning: return "";
                case StepState.Skipped: return "";
                default: return "";
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class StepBrush : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string key;
            switch (value as StepState? ?? StepState.Pending)
            {
                case StepState.Done: key = "Good"; break;
                case StepState.Active: key = "Accent"; break;
                case StepState.Failed: key = "Bad"; break;
                case StepState.Warning: key = "Warn"; break;
                case StepState.Skipped: key = "TextMute"; break;
                default: key = "Bg4"; break;
            }
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class Bytes : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double b = System.Convert.ToDouble(value ?? 0);
            if (b <= 0) return "";
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return b.ToString(i >= 2 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + u[i];
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Number of grid columns that fit a width (parameter = minimum column width).</summary>
    public sealed class ColumnsFor : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double w = value is double d ? d : 0;
            double min = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 220;
            return Math.Max(1, (int)(w / min));
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public sealed class Upper : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value?.ToString() ?? "").ToUpper(CultureInfo.CurrentCulture);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Brush for a status kind (Ready/Match/Busy/Bad/Off); parameter picks bg, border, dot or fg.</summary>
    public sealed class StatusBrush : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string kind = value as string ?? (value is bool b ? (b ? "Ready" : "Bad") : "Off");
            string part = parameter as string ?? "fg";
            string key;
            switch (kind)
            {
                case "Ready": key = part == "bg" ? "GoodBg" : part == "border" ? "GoodBorder" : part == "dot" ? "Good" : "GoodFg"; break;
                case "Match": key = part == "bg" ? "InfoBg" : part == "border" ? "InfoBorder" : part == "dot" ? "Info" : "InfoFg"; break;
                case "Busy": key = part == "bg" ? "AccentSoftBg" : part == "border" ? "AccentBorder" : part == "dot" ? "Accent" : "AccentSoftFg"; break;
                case "Bad": key = part == "bg" ? "BadBg" : part == "border" ? "BadBorder" : part == "dot" ? "Bad" : "BadFg"; break;
                default: key = part == "bg" ? "Bg2" : part == "border" ? "Line" : part == "dot" ? "TextFaint" : "TextDim"; break;
            }
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
