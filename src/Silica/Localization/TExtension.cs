using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace Silica.Localization;

/// <summary>
/// XAML: Text="{l:T Dashboard}" or Text="{l:T 'Run all tests'}". Updates live when the language changes.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public TExtension() : this("") { }

    [ConstructorArgument("key")]
    public string Key { get; set; } = key;

    /// <summary>Optional: transform the result (Upper for section captions).</summary>
    public CharacterCasing Casing { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(Loc.Revision))
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            Converter = KeyConverter.Instance,
            ConverterParameter = this
        };
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class KeyConverter : IValueConverter
    {
        public static readonly KeyConverter Instance = new();

        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        {
            var ext = (TExtension)parameter!;
            var text = Loc.Instance.T(ext.Key);
            return ext.Casing switch
            {
                CharacterCasing.Upper => text.ToUpper(CultureInfo.CurrentUICulture),
                CharacterCasing.Lower => text.ToLower(CultureInfo.CurrentUICulture),
                _ => text
            };
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

/// <summary>Converter for bound keys: {Binding Title, Converter={StaticResource Tr}}.</summary>
public sealed class TranslateConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? Loc.Instance.T(s) : value ?? "";

    // Multi-binding form lets the second binding be Loc.Revision so the text refreshes on language change.
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 0 && values[0] is string s ? Loc.Instance.T(s) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
