using System;
using Microsoft.UI.Xaml.Data;

namespace CryptoTax2026.Converters;

public class CurrencyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is decimal d)
            return d.ToString("£#,##0.00");
        return "£0.00";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is string s)
        {
            s = s.Replace("£", "").Replace(",", "").Trim();
            if (decimal.TryParse(s, out var result))
                return result;
        }
        return 0m;
    }
}

public class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is decimal d)
            return (d * 100).ToString("0.#") + "%";
        return "0%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
