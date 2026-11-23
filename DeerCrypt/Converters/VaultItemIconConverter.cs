using Avalonia.Data.Converters;
using DeerCrypt.Services;
using DeerCrypt.ViewModels;
using DeerCryptLib.Vault;
using System;
using System.Globalization;

namespace DeerCrypt.Converters
{
    public class VaultItemIconConverter : IValueConverter
    {
        public static readonly VaultItemIconConverter Instance = new();

        /// <summary>Returns the Fluent icon symbol for a vault item.</summary>
        public object Convert( object? value, Type targetType, object? parameter, CultureInfo culture )
        {
            if( value is VaultItemViewModel vm )
                return vm.Icon;
            if( value is VaultDirectoryEntry )
                return FileKindVisuals.GetIcon( FileKind.Folder );
            if( value is VaultEntry entry )
                return FileKindVisuals.GetIcon( FileTypeHelper.GetKind( entry.Name ) );
            return FileKindVisuals.GetIcon( FileKind.Other );
        }

        public object ConvertBack( object? value, Type targetType, object? parameter, CultureInfo culture )
            => throw new NotImplementedException( );
    }
}