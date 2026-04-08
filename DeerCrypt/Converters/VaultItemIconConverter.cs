using Avalonia.Data.Converters;
using DeerCrypt.ViewModels;
using DeerCryptLib.Vault;
using System;
using System.Globalization;

namespace DeerCrypt.Converters
{
    public class VaultItemIconConverter : IValueConverter
    {
        public static readonly VaultItemIconConverter Instance = new();

        public object Convert( object? value, Type targetType, object? parameter, CultureInfo culture )
        {
            if( value is VaultItemViewModel vm )
                return vm.IsDir ? "📁" : "📄";
            if( value is VaultDirectoryEntry )
                return "📁";
            return "📄";
        }

        public object ConvertBack( object? value, Type targetType, object? parameter, CultureInfo culture )
            => throw new NotImplementedException( );
    }
}