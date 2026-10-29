namespace MangaLibraryApp;

/// <summary>Gespeicherte Anwendungseinstellungen aus der Tabelle <c>settings</c>.</summary>
public class AppSettings
{
    public string LibraryPath { get; set; } = string.Empty;
    public string GalleryDlPath { get; set; } = string.Empty;
    public string ExtraArgs { get; set; } = string.Empty;
    public int MaxParallel { get; set; } = 2;
    public string Sort { get; set; } = string.Empty;
    public string EnabledSources { get; set; } = string.Empty;
    public string FlareSolverrUrl { get; set; } = string.Empty;

    public string EHentaiIpbMemberId { get; set; } = string.Empty;
    public string EHentaiIpbPassHash { get; set; } = string.Empty;
    public string EHentaiCfClearance { get; set; } = string.Empty;

    /// <summary>
    /// Bevorzugter Browser für Cookie-Extraction: "auto", "chrome", "edge", "firefox", "brave", "vivaldi"
    /// </summary>
    public string PreferredBrowser { get; set; } = "auto";

    /// <summary><c>auto</c>, <c>en</c> oder <c>de</c>.</summary>
    public string UiLanguage { get; set; } = "auto";

    /// <summary>Autovervollständigung und Katalog ergänzen die Hitomi-Tagbank, wenn der Schalter in der Websuche an ist.</summary>
    public bool UseGlobalTagFallback { get; set; }

    public void Load(Database db)
    {
        LibraryPath = db.GetSetting("LibraryPath") ?? string.Empty;
        GalleryDlPath = db.GetSetting("GalleryDlPath") ?? string.Empty;
        ExtraArgs = db.GetSetting("ExtraArgs") ?? string.Empty;
        MaxParallel = int.TryParse(db.GetSetting("MaxParallel"), out var parallel) ? Math.Clamp(parallel, 1, 4) : 2;
        Sort = db.GetSetting("Sort") ?? string.Empty;
        EnabledSources = db.GetSetting("EnabledSources") ?? string.Empty;
        FlareSolverrUrl = db.GetSetting("FlareSolverrUrl") ?? string.Empty;
        EHentaiIpbMemberId = db.GetSetting(nameof(EHentaiIpbMemberId)) ?? db.GetSetting("EhIpbMemberId") ?? string.Empty;
        EHentaiIpbPassHash = db.GetSetting(nameof(EHentaiIpbPassHash)) ?? db.GetSetting("EhIpbPassHash") ?? string.Empty;
        EHentaiCfClearance = db.GetSetting(nameof(EHentaiCfClearance)) ?? db.GetSetting("EhCfClearance") ?? string.Empty;
        PreferredBrowser = NormalizeBrowser(db.GetSetting(nameof(PreferredBrowser)));
        UiLanguage = NormalizeLanguage(db.GetSetting(nameof(UiLanguage)));
        UseGlobalTagFallback = string.Equals(db.GetSetting(nameof(UseGlobalTagFallback)), "1", StringComparison.Ordinal);
    }

    private static string NormalizeBrowser(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text is "chrome" or "edge" or "firefox" or "brave" or "vivaldi" ? text : "auto";
    }

    private static string NormalizeLanguage(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text is "en" or "de" ? text : "auto";
    }

    public void Save(Database db)
    {
        db.SetSetting("LibraryPath", LibraryPath);
        db.SetSetting("GalleryDlPath", GalleryDlPath);
        db.SetSetting("ExtraArgs", ExtraArgs);
        db.SetSetting("MaxParallel", MaxParallel.ToString());
        db.SetSetting("Sort", Sort);
        db.SetSetting("EnabledSources", EnabledSources);
        db.SetSetting("FlareSolverrUrl", string.IsNullOrWhiteSpace(FlareSolverrUrl) ? null : FlareSolverrUrl);
        db.SetSetting(nameof(EHentaiIpbMemberId), EHentaiIpbMemberId);
        db.SetSetting(nameof(EHentaiIpbPassHash), EHentaiIpbPassHash);
        db.SetSetting(nameof(EHentaiCfClearance), EHentaiCfClearance);
        db.SetSetting(nameof(PreferredBrowser), string.IsNullOrWhiteSpace(PreferredBrowser) ? "auto" : PreferredBrowser);
        db.SetSetting(nameof(UiLanguage), string.IsNullOrWhiteSpace(UiLanguage) ? "auto" : UiLanguage);
        db.SetSetting(nameof(UseGlobalTagFallback), UseGlobalTagFallback ? "1" : "0");
    }
}
