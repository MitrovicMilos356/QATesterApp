using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UiInspector.App;

/// <summary>Visible when the bound page number equals the parameter: one page of the rail at a time.</summary>
public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int page && int.TryParse(parameter as string, out var mine) && page == mine ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the bound count, flag or item is zero, false or null: the empty state of a list.</summary>
public sealed class EmptyVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null or 0 or false ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
