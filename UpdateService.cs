using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace MangaLibraryApp;

public sealed class UpdateNotice
{
    public string Product { get; init; } = string.Empty;
    public string LatestVersion { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public bool IsGalleryDl { get; init; }
}

/// <summary>Vergleicht NekoDex und gallery-dl mit den aktuellen GitHub-Releases.</summary>
public static class UpdateService
{
    public const string NekoDexReleasesUrl = "https://api.github.com/repos/YOUR_REPO/NekoDex/releases/latest";
    public const string GalleryDlReleasesUrl = "https://api.github.com/repos/mikf/gallery-dl/releases/latest";

    public static Version CurrentAppVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static async Task<IReadOnlyList<UpdateNotice>> CheckAsync(string? galleryDlVersion, CancellationToken cancellationToken = default)
    {
        var notices = new List<UpdateNotice>();
        var app = await ReadReleaseAsync(NekoDexReleasesUrl, cancellationToken);
        if (app is { } appRelease && IsNewer(appRelease.Version, CurrentAppVersion))
        {
            notices.Add(new UpdateNotice
            {
                Product = "NekoDex",
                LatestVersion = appRelease.Tag,
                Url = appRelease.Url,
            });
        }

        var tool = await ReadReleaseAsync(GalleryDlReleasesUrl, cancellationToken);
        var local = LeadingVersion(galleryDlVersion);
        if (tool is { } toolRelease && local is not null && IsNewer(toolRelease.Version, local))
        {
            notices.Add(new UpdateNotice
            {
                Product = "gallery-dl",
                LatestVersion = toolRelease.Tag,
                Url = toolRelease.Url,
                IsGalleryDl = true,
            });
        }

        return notices;
    }

    private static bool IsNewer(Version latest, Version current) => latest > current;

    private static async Task<(string Tag, Version Version, string Url)?> ReadReleaseAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NekoDex");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var html = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
            var version = LeadingVersion(tag);
            if (version is null || string.IsNullOrWhiteSpace(html))
                return null;
            return (tag ?? version.ToString(), version, html);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static Version? LeadingVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var start = 0;
        while (start < text.Length && !char.IsDigit(text[start]))
            start++;
        var end = start;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.'))
            end++;
        var slice = text[start..end].Trim('.');
        if (slice.Length == 0)
            return null;
        if (!slice.Contains('.'))
            slice += ".0";
        return Version.TryParse(slice, out var version) ? version : null;
    }
}
