using System.Globalization;
using CodexBarWindows.Converters;
using System.Windows;

namespace CodexBarWindows.WpfSmokeTests;

public class ProgressConverterTests
{
    [Fact]
    public void Progress_converter_maps_double_values_to_star_grid_lengths()
    {
        var converter = new ProgressToGridLengthConverter();

        var result = (GridLength)converter.Convert(0.25, typeof(GridLength), null!, CultureInfo.InvariantCulture);

        Assert.Equal(0.25, result.Value, 3);
        Assert.Equal(GridUnitType.Star, result.GridUnitType);
    }

    [Fact]
    public void Progress_converter_returns_zero_for_invalid_values()
    {
        var converter = new ProgressToGridLengthConverter();

        var result = (GridLength)converter.Convert("not-a-number", typeof(GridLength), null!, CultureInfo.InvariantCulture);

        Assert.Equal(0, result.Value);
        Assert.Equal(GridUnitType.Star, result.GridUnitType);
    }

    [Fact]
    public void Remaining_converter_inverts_double_values()
    {
        var converter = new RemainingToGridLengthConverter();

        var result = (GridLength)converter.Convert(0.25, typeof(GridLength), null!, CultureInfo.InvariantCulture);

        Assert.Equal(0.75, result.Value, 3);
        Assert.Equal(GridUnitType.Star, result.GridUnitType);
    }

    [Fact]
    public void Remaining_converter_returns_full_star_for_invalid_values()
    {
        var converter = new RemainingToGridLengthConverter();

        var result = (GridLength)converter.Convert(new object(), typeof(GridLength), null!, CultureInfo.InvariantCulture);

        Assert.Equal(1.0, result.Value, 3);
        Assert.Equal(GridUnitType.Star, result.GridUnitType);
    }

    [Fact]
    public void ConvertBack_is_not_implemented_for_both_converters()
    {
        var progressConverter = new ProgressToGridLengthConverter();
        var remainingConverter = new RemainingToGridLengthConverter();

        Assert.Throws<NotImplementedException>(() => progressConverter.ConvertBack(new GridLength(1, GridUnitType.Star), typeof(double), null!, CultureInfo.InvariantCulture));
        Assert.Throws<NotImplementedException>(() => remainingConverter.ConvertBack(new GridLength(1, GridUnitType.Star), typeof(double), null!, CultureInfo.InvariantCulture));
    }
}
