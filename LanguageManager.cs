using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace MangaLibraryApp;

/// <summary>Wechselt die Oberflächensprache zur Laufzeit und liefert die Texte aus <c>Resources/Strings.resx</c>.</summary>
public sealed class LanguageManager : INotifyPropertyChanged
{
    /// <summary>Muss vor <see cref="Instance"/> stehen. Der Instanz-Konstruktor liest die Systemkultur.</summary>
    private static readonly CultureInfo SystemUi = CaptureSystemCulture();

    private static LanguageManager? _instance;

    private readonly ResourceManager? _resources;

    private CultureInfo _culture;

    static LanguageManager()
    {
        try
        {
            _instance = new LanguageManager();
        }
        catch (Exception ex) when (ex is NullReferenceException or CultureNotFoundException or MissingManifestResourceException)
        {
            _instance = new LanguageManager(bare: true);
        }
        catch
        {
            _instance = new LanguageManager(bare: true);
        }
    }

    private LanguageManager()
        : this(bare: false)
    {
    }

    private LanguageManager(bool bare)
    {
        _resources = bare ? null : TryCreateResources();
        _culture = SafeResolve("auto");
    }

    public static LanguageManager Instance => _instance ??= new LanguageManager(bare: true);

    /// <summary>Vor dem Hauptfenster aufrufen, damit der XAML-Parser keine Typinitialisierung mehr anstößt.</summary>
    public static void EnsureInitialized()
    {
        try
        {
            _ = Instance;
        }
        catch
        {
            _instance ??= new LanguageManager(bare: true);
        }
    }

    public static bool IsInDesignMode
    {
        get
        {
            try
            {
                return DesignerProperties.GetIsInDesignMode(new DependencyObject());
            }
            catch
            {
                return false;
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? Changed;

    public CultureInfo Culture => _culture;

    public string this[string key] => GetString(key) ?? key;

    public string? GetString(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;
        if (IsInDesignMode)
            return key;

        try
        {
            if (_resources is null)
                return key;

            return _resources.GetString(key, _culture)
                   ?? _resources.GetString(key, CultureInfo.InvariantCulture)
                   ?? _resources.GetString(key)
                   ?? key;
        }
        catch (Exception ex) when (ex is NullReferenceException or MissingManifestResourceException or InvalidOperationException)
        {
            return key;
        }
        catch
        {
            return key;
        }
    }

    public string Get(string key, params object[] args)
    {
        var value = GetString(key) ?? key;
        if (args is not { Length: > 0 })
            return value;

        try
        {
            return string.Format(_culture, value, args);
        }
        catch (Exception ex) when (ex is FormatException or NullReferenceException)
        {
            return value;
        }
    }

    public void Apply(string? preference) => SetCulture(SafeResolve(preference));

    public void SetCulture(CultureInfo culture)
    {
        _culture = culture ?? CultureInfo.InvariantCulture;
        try
        {
            CultureInfo.CurrentUICulture = _culture;
            CultureInfo.DefaultThreadCurrentUICulture = _culture;
        }
        catch
        {
            _culture = CultureInfo.InvariantCulture;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    public static CultureInfo Resolve(string? preference) => SafeResolve(preference);

    private static CultureInfo SafeResolve(string? preference)
    {
        try
        {
            var key = (preference ?? "auto").Trim().ToLowerInvariant();
            if (key is "en" or "english")
                return CultureOrEnglish("en");
            if (key is "de" or "deutsch" or "german")
                return CultureOrEnglish("de");

            var ui = SystemUi ?? CultureInfo.InvariantCulture;
            var name = ui.TwoLetterISOLanguageName;
            return string.Equals(name, "de", StringComparison.OrdinalIgnoreCase)
                ? CultureOrEnglish("de")
                : CultureOrEnglish("en");
        }
        catch (Exception ex) when (ex is NullReferenceException or CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
        catch
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static CultureInfo CultureOrEnglish(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static CultureInfo CaptureSystemCulture()
    {
        try
        {
            return CultureInfo.CurrentUICulture ?? CultureInfo.InstalledUICulture ?? CultureInfo.InvariantCulture;
        }
        catch
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static ResourceManager? TryCreateResources()
    {
        try
        {
            return new ResourceManager("MangaLibraryApp.Resources.Strings", typeof(LanguageManager).Assembly);
        }
        catch
        {
            return null;
        }
    }
}
