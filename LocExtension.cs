using System.Windows.Data;
using System.Windows.Markup;

namespace MangaLibraryApp;

/// <summary>Laufzeit-Text: <c>{lex:Loc Nav_Library}</c>. Wechselt mit <see cref="LanguageManager.SetCulture"/>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key))
            return string.Empty;

        if (LanguageManager.IsInDesignMode)
            return Key;

        try
        {
            var manager = LanguageManager.Instance;
            if (manager == null)
                return Key;

            var binding = new Binding("[" + Key + "]")
            {
                Source = manager,
                Mode = BindingMode.OneWay,
                FallbackValue = manager.GetString(Key) ?? Key,
                TargetNullValue = Key,
            };
            return binding.ProvideValue(serviceProvider);
        }
        catch
        {
            try
            {
                return LanguageManager.Instance?.GetString(Key) ?? Key;
            }
            catch
            {
                return Key;
            }
        }
    }
}
