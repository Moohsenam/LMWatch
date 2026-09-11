using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ClaudeWatch.Core;

namespace ClaudeWatch.App;

/// <summary>Colours an activity row by how serious it is.</summary>
public sealed class KindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is ActivityKind kind
            ? kind switch
            {
                ActivityKind.Good => "Good",
                ActivityKind.Warning => "Warn",
                ActivityKind.Alert => "Alert",
                _ => "Info"
            }
            : "TextDim";

        if (parameter is string suffix && suffix == "soft")
        {
            key += "Soft";
        }

        return MainViewModel.Palette(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>True collapses, false shows — the mirror of the built-in converter.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Shows an element only while a list is empty.</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value switch
        {
            ICollection collection => collection.Count,
            int number => number,
            _ => 0
        };

        var invert = parameter is string text && text == "invert";
        var empty = count == 0;
        return (invert ? !empty : empty) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Green when an adapter currently counts as the VPN, muted otherwise.</summary>
public sealed class AdapterBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var counts = value is bool flag && flag;
        return MainViewModel.Palette(counts ? "Good" : "TextFaint");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Turns a hex string into a brush for the accent swatches.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is string hex && hex.Length > 0)
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
        }
        catch
        {
        }

        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

// ------------------------------------------------------------------ the buy page
//
// A price card needs the plan's name, price, period and note in the user's own
// language. PricedPlan lives in Core and has no idea which language is on, so
// these four ask the view model rather than making Core carry a Strings table.

/// <summary>The plan's name, Persian or English.</summary>
public sealed class PlanNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PricedPlan plan ? plan.Name(Persian) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    internal static bool Persian => App.Model?.L.IsRightToLeft == true;
}

/// <summary>The toman figure, grouped with the right digits for the language.</summary>
public sealed class PlanPriceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PricedPlan plan
            ? PricingClient.Money(plan.Toman, PlanNameConverter.Persian)
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>"per month", "per year", "per person, per month".</summary>
public sealed class PlanPeriodConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not PricedPlan plan || App.Model is not { } model)
        {
            return string.Empty;
        }

        return plan.Period switch
        {
            "month" => model.L["Buy_PerMonth"],
            "year" => model.L["Buy_PerYear"],
            "seat" => model.L["Buy_PerSeat"],
            _ => string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>The plan's own small print, if it has any.</summary>
public sealed class PlanNoteConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PricedPlan plan ? plan.Small(PlanNameConverter.Persian) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
