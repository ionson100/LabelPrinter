using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LabelPrinter.Protocol;
using LabelPrinter.Services;

namespace LabelPrinter.Converters
{
    /// <summary>Цвет лампочки: зелёный — связь есть, красный — нет.</summary>
    public sealed class ConnectedToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool connected = value is bool && (bool)value;
            return new SolidColorBrush(connected ? Color.FromRgb(0x2E, 0xC4, 0x6B) : Color.FromRgb(0xE0, 0x3A, 0x3A));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    /// <summary>Читаемая подпись для лампочки.</summary>
    public sealed class ConnectedToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool connected = value is bool && (bool)value;
            return connected ? "подключён" : "нет связи";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    /// <summary>Индикатор режима печати.</summary>
    public sealed class PrintingToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool printing = value is bool && (bool)value;
            return new SolidColorBrush(printing ? Color.FromRgb(0x2E, 0xC4, 0x6B) : Color.FromRgb(0x8A, 0x8A, 0x8A));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    /// <summary>Строка состояния соединения принтера.</summary>
    public sealed class LinkStateToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var runtime = value as PrinterRuntime;
            if (runtime == null) return string.Empty;
            return runtime.LinkText ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    /// <summary>Текст кнопки печати: «Печать» / «Отмена».</summary>
    public sealed class PrintingToButtonTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool printing = value is bool && (bool)value;
            return printing ? "Отмена" : "Печать";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    /// <summary>true, когда печать идёт — для подсветки кнопки.</summary>
    public sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !(value is bool && (bool)value);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    /// <summary>Заменяет null и пустую строку на прочерк.</summary>
    public sealed class EmptyToDashConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "—";
            var text = value.ToString();
            return string.IsNullOrWhiteSpace(text) ? "—" : text;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    /// <summary>Обрезает длинный GS1-код до читаемого вида.</summary>
    public sealed class CodeToDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "—";
            var text = value.ToString();
            if (string.IsNullOrEmpty(text)) return "—";
            return Codes.CodeFactory.ToHumanReadable(text);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }
}
