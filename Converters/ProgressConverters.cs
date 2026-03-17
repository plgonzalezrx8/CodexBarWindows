using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CodexBarWindows.Converters;

public class ProgressToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double progress)
        {
            // progress is 0.0 to 1.0
            return new GridLength(progress, GridUnitType.Star);
        }
        return new GridLength(0, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class RemainingToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double progress)
        {
            // progress is 0.0 to 1.0
            return new GridLength(1.0 - progress, GridUnitType.Star);
        }
        return new GridLength(1.0, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
