using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using NekoDex.Services.Scrapers;
using PlatformSource = NekoDex.Core.Enums.SourceProvider;

namespace MangaLibraryApp;

/// <summary>Ein Tab oder ein Eintrag unter „Weitere Quellen…“.</summary>
public sealed class WebSourceOption : ObservableObject
{
    private bool _isSelected;

    public PlatformSource Provider { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Kennung der bestehenden Suche (<c>nhentai</c>, <c>ehentai</c>, <c>hitomi</c>).</summary>
    public string? SiteId { get; init; }

    public bool IsImplemented { get; init; }

    public bool IsPlaceholder { get; init; }

    public IScraperProvider? Scraper { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}

/// <summary>Quellen der Websuche: registrierte Scraper als Tabs, der Rest des Enums als Roadmap.</summary>
public sealed class WebSearchViewModel : ObservableObject
{
    private bool _sync;
    private int _version;
    private string _siteId = string.Empty;
    private WebSourceOption? _selected;
    private WebSourceOption? _selectedRoadmap;

    /// <summary>Lädt die Tags einer Kategorie im Hintergrund. Wird vom Fenster mit der Datenbank verbunden.</summary>
    public Func<string, string, IReadOnlyList<TagChip>>? CategoryLoader { get; set; }

    public event Action? CategoryLoaded;

    public WebSearchViewModel(IScraperFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var registered = factory.GetAllProviders().ToDictionary(provider => provider.Provider);
        var roadmap = new List<WebSourceOption>
        {
            new() { Title = "Weitere Quellen...", IsPlaceholder = true },
        };

        foreach (var provider in Enum.GetValues<PlatformSource>().OrderBy(value => (int)value))
        {
            if (registered.TryGetValue(provider, out var scraper))
            {
                ActiveSources.Add(new WebSourceOption
                {
                    Provider = provider,
                    Title = TitleFor(provider),
                    SiteId = SiteIdFor(provider),
                    IsImplemented = true,
                    Scraper = scraper,
                });
            }
            else
            {
                roadmap.Add(new WebSourceOption
                {
                    Provider = provider,
                    Title = TitleFor(provider),
                    IsImplemented = false,
                });
            }
        }

        foreach (var option in roadmap)
            RoadmapSources.Add(option);

        if (ActiveSources.Count > 0)
        {
            ActiveSources[0].IsSelected = true;
            _selected = ActiveSources[0];
        }

        _selectedRoadmap = RoadmapSources[0];
    }

    public ObservableCollection<WebSourceOption> ActiveSources { get; } = new();

    public ObservableCollection<WebSourceOption> RoadmapSources { get; } = new();

    /// <summary>Online-Tags der gewählten Quelle, flach.</summary>
    public ObservableCollection<TagChip> TagsCollection { get; } = new();

    /// <summary>Dieselben Tags, nach Kategorie gruppiert, für die Sidebar.</summary>
    public ObservableCollection<TagGroup> TagGroups { get; } = new();

    public WebSourceOption? Selected => _selected;

    public WebSourceOption? SelectedRoadmap
    {
        get => _selectedRoadmap;
        set => SetField(ref _selectedRoadmap, value);
    }

    public bool CanSearch => _selected?.IsImplemented == true && !string.IsNullOrEmpty(_selected.SiteId);

    public string RoadmapBanner =>
        _selected is { IsImplemented: false, IsPlaceholder: false } option
            ? option.Title + " ist in der Roadmap eingeplant, aber aktuell noch nicht implementiert."
            : string.Empty;

    public Visibility RoadmapBannerVisibility =>
        RoadmapBanner.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public IReadOnlyList<string> BlacklistedTags { get; private set; } = Array.Empty<string>();

    public event Action? SelectionChanged;

    public void SetBlacklist(IEnumerable<string> tags) =>
        BlacklistedTags = tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Ausschluss-Begriffe für Quellen, deren Suche <c>-tag</c> versteht. Hitomi filtert nur die Trefferliste.</summary>
    public IReadOnlyList<string> ExclusionQuery(string site)
    {
        if (site is not ("nhentai" or "ehentai"))
            return Array.Empty<string>();

        return BlacklistedTags
            .Select(tag => "-" + tag.Trim().TrimStart('-'))
            .ToList();
    }

    public bool Allows(IEnumerable<string> hitTags)
    {
        if (BlacklistedTags.Count == 0)
            return true;

        foreach (var tag in hitTags)
        {
            if (IsBlocked(tag))
                return false;
        }

        return true;
    }

    private bool IsBlocked(string tag)
    {
        var normalized = TagNames.Normalize(tag);
        var (_, name) = TagNames.Split(normalized);
        foreach (var blocked in BlacklistedTags)
        {
            var blockedNorm = TagNames.Normalize(blocked);
            var (_, blockedName) = TagNames.Split(blockedNorm);
            if (blockedNorm.Length > 0 && string.Equals(normalized, blockedNorm, StringComparison.OrdinalIgnoreCase))
                return true;
            if (blockedName.Length > 0 && string.Equals(name, blockedName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Leert Sidebar und Katalog, ohne Tag-Zeilen zu lesen.</summary>
    public void ClearTags()
    {
        _version++;
        TagsCollection.Clear();
        TagGroups.Clear();
    }

    /// <summary>
    /// Zeigt nur die Kategorie-Köpfe, alle eingeklappt. Die Tags einer Kategorie kommen erst in <see cref="LoadGroupAsync"/>.
    /// </summary>
    public void ShowCategories(string siteId, IReadOnlyList<TagCategorySummary> categories)
    {
        _siteId = siteId ?? string.Empty;
        var version = ++_version;
        var site = _siteId;
        TagsCollection.Clear();
        TagGroups.Clear();

        foreach (var summary in categories
                     .OrderBy(item => TagCategoryUi.SortKey(item.Category))
                     .ThenBy(item => TagCategoryUi.Title(item.Category), StringComparer.OrdinalIgnoreCase))
        {
            var group = new TagGroup(summary.Category, Array.Empty<TagChip>(), expanded: false);
            group.SetCount(summary.Count);
            group.ExpandRequested += item => _ = LoadGroupAsync(item, version, site);
            TagGroups.Add(group);
        }
    }

    private async Task LoadGroupAsync(TagGroup group, int version, string siteId)
    {
        if (group.TagsLoaded || group.IsLoading || CategoryLoader is null)
            return;

        group.IsLoading = true;
        IReadOnlyList<TagChip> chips;
        try
        {
            var category = group.Category;
            chips = await Task.Run(() => CategoryLoader(siteId, category)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            group.IsLoading = false;
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            group.IsLoading = false;
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            if (version != _version || !string.Equals(siteId, _siteId, StringComparison.Ordinal))
            {
                group.IsLoading = false;
                return;
            }

            group.Fill(chips);
            foreach (var chip in chips)
                TagsCollection.Add(chip);
            CategoryLoaded?.Invoke();
        }, DispatcherPriority.Background);
    }

    /// <summary>Ersetzt Sidebar und Katalog durch die Tags genau einer Quelle. Eine leere Liste leert beide.</summary>
    public void ReplaceTags(IEnumerable<TagInfo> tags)
    {
        var list = tags as IReadOnlyList<TagInfo> ?? tags.ToList();
        if (list.Count == 0)
        {
            ClearTags();
            return;
        }

        ShowCategories(_siteId, list
            .GroupBy(tag => tag.Category ?? string.Empty, StringComparer.Ordinal)
            .Select(group => new TagCategorySummary(group.Key, group.Count()))
            .ToList());
    }

    public void SelectActive(WebSourceOption option)
    {
        if (_sync || !option.IsImplemented)
            return;

        _sync = true;
        foreach (var item in ActiveSources)
            item.IsSelected = ReferenceEquals(item, option);
        SelectedRoadmap = RoadmapSources.Count > 0 ? RoadmapSources[0] : null;
        _selected = option;
        _sync = false;
        Publish();
    }

    public void SelectRoadmap(WebSourceOption option)
    {
        if (_sync || option.IsPlaceholder)
            return;

        _sync = true;
        foreach (var item in ActiveSources)
            item.IsSelected = false;
        _selected = option;
        _sync = false;
        Publish();
    }

    private void Publish()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(RoadmapBanner));
        OnPropertyChanged(nameof(RoadmapBannerVisibility));
        SelectionChanged?.Invoke();
    }

    public static string? SiteIdFor(PlatformSource provider) => provider switch
    {
        PlatformSource.NHentai => "nhentai",
        PlatformSource.EHentai => "ehentai",
        PlatformSource.ExHentai => "exhentai",
        PlatformSource.Hitomi => "hitomi",
        PlatformSource.SimplyHentai => "simplyhentai",
        PlatformSource.HentaiFox => "hentaifox",
        PlatformSource.Asmhentai => "asmhentai",
        PlatformSource.KemonoSu => "kemono",
        PlatformSource.HentaiHand => "hentaihand",
        PlatformSource.HentaiEra => "hentaiera",
        PlatformSource.HentaiNexus => "hentainexus",
        PlatformSource.ThreeHentai => "3hentai",
        PlatformSource.LoFiEHentai => "lofiehentai",
        PlatformSource.HentaiDB => "hentaidb",
        PlatformSource.DoujinsCom => "doujins",
        PlatformSource.HentaiEnvy => "hentaienvy",
        PlatformSource.HentaiClap => "hentaiclap",
        PlatformSource.HentaiRun => "hentairun",
        _ => null,
    };

    public static string TitleFor(PlatformSource provider) => provider switch
    {
        PlatformSource.NHentai => "nhentai",
        PlatformSource.EHentai => "E-Hentai",
        PlatformSource.ExHentai => "ExHentai",
        PlatformSource.Hitomi => "Hitomi.la",
        PlatformSource.SimplyHentai => "SimplyHentai",
        PlatformSource.HentaiFox => "HentaiFox",
        PlatformSource.Asmhentai => "AsmHentai",
        PlatformSource.HentaiHand => "HentaiHand",
        PlatformSource.HentaiEra => "HentaiEra",
        PlatformSource.HentaiNexus => "HentaiNexus",
        PlatformSource.ThreeHentai => "3Hentai",
        PlatformSource.LoFiEHentai => "LoFi E-Hentai",
        PlatformSource.HentaiDB => "HentaiDB",
        PlatformSource.KemonoSu => "Kemono",
        PlatformSource.DoujinsCom => "Doujins.com",
        PlatformSource.HentaiEnvy => "HentaiEnvy",
        PlatformSource.HentaiClap => "HentaiClap",
        PlatformSource.HentaiRun => "HentaiRun",
        _ => provider.ToString(),
    };
}
