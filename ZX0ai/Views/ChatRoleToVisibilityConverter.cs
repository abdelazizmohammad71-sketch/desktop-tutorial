using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using ZX0ai.Core.Models;

namespace ZX0ai.Views;

public sealed class ChatRoleToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not ChatRole role || parameter is not string expected)
        {
            return Visibility.Collapsed;
        }

        return role.ToString() == expected
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
