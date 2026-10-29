using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace MangaLibraryApp;

/// <summary>Fortschritt beim Laden von gallery-dl.exe.</summary>
public readonly record struct GalleryDlDownloadUpdate(long Received, long Total, string Status);

/// <summary>Prüft beim Start, ob gallery-dl lokal liegt, und lädt sonst die Windows-Datei nach.</summary>
public static class GalleryDlService
{
    /// <summary>Offizielle Latest-URL. Das GitHub-Release enthält seit dem Umzug nach Codeberg oft keine Datei.</summary>
    public const string OfficialDownloadUrl = "https://github.com/mikf/gallery-dl/releases/latest/download/gallery-dl.exe";

    private const string GitHubReleaseApi = "https://api.github.com/repos/mikf/gallery-dl/releases/latest";
    private const string UserAgent = "NekoDex-App";

    private static readonly Regex ReleaseTag = new(
        "\"tag_name\"\\s*:\\s*\"(?<tag>v[^\"]+)\"",
        RegexOptions.Compiled);

    public static string BundledPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MangaLibraryApp",
        "tools",
        "gallery-dl.exe");

    /// <summary><c>./tools/gallery-dl.exe</c> neben der Programmdatei.</summary>
    public static string ToolsPath
    {
        get
        {
            var process = Environment.ProcessPath;
            var directory = string.IsNullOrEmpty(process) ? null : Path.GetDirectoryName(process);
            if (string.IsNullOrEmpty(directory))
                directory = AppContext.BaseDirectory;
            return Path.Combine(directory, "tools", "gallery-dl.exe");
        }
    }

    public static async Task EnsureAsync(string? configuredPath = null)
    {
        try
        {
            var (command, problem) = await GalleryDl.ResolveAsync(configuredPath);
            if (command is not null)
            {
                App.Log("gallery-dl bereit: " + command.Description);
                return;
            }

            App.Log("gallery-dl nicht gefunden (" + (problem ?? "kein Pfad") + "). Lade " + OfficialDownloadUrl);
            var saved = await DownloadAsync();
            App.Log(saved is null
                ? "gallery-dl konnte nicht geladen werden."
                : "gallery-dl gespeichert: " + saved);
        }
        catch (Exception ex)
        {
            App.Log("gallery-dl-Prüfung: " + ex.Message);
        }
    }

    public static async Task<string?> DownloadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await DownloadGalleryDlAsync(progress: null, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            App.Log("gallery-dl-Download: " + ex.Message);
            return null;
        }
    }

    public static async Task<string> DownloadGalleryDlAsync(
        IProgress<GalleryDlDownloadUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ToolsPath)!);
        var temporary = ToolsPath + ".download";
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        var url = await ResolveDownloadUrlAsync(http, progress, cancellationToken);
        progress?.Report(new GalleryDlDownloadUpdate(0, 0, "Lade gallery-dl …"));
        await DownloadFileAsync(http, url, temporary, progress, cancellationToken);

        if (new FileInfo(temporary).Length < 100_000)
        {
            File.Delete(temporary);
            throw new InvalidOperationException("Die heruntergeladene gallery-dl.exe ist unvollständig.");
        }

        File.Move(temporary, ToolsPath, overwrite: true);
        progress?.Report(new GalleryDlDownloadUpdate(1, 1, "gallery-dl gespeichert."));
        return ToolsPath;
    }

    private static async Task<string> ResolveDownloadUrlAsync(
        HttpClient http,
        IProgress<GalleryDlDownloadUpdate>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new GalleryDlDownloadUpdate(0, 0, "Prüfe GitHub-Release …"));
        using (var probe = new HttpRequestMessage(HttpMethod.Get, OfficialDownloadUrl))
        using (var response = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!type.Contains("html", StringComparison.OrdinalIgnoreCase))
                    return OfficialDownloadUrl;
            }
        }

        progress?.Report(new GalleryDlDownloadUpdate(0, 0, "GitHub ohne Datei, suche das aktuelle Release …"));
        using var api = await http.GetAsync(GitHubReleaseApi, cancellationToken);
        api.EnsureSuccessStatusCode();
        var json = await api.Content.ReadAsStringAsync(cancellationToken);
        var tag = ReleaseTag.Match(json);
        if (!tag.Success)
            throw new InvalidOperationException("Das aktuelle gallery-dl-Release nennt keine Version.");

        return "https://codeberg.org/mikf/gallery-dl/releases/download/" + tag.Groups["tag"].Value + "/gallery-dl.exe";
    }

    private static async Task DownloadFileAsync(
        HttpClient http,
        string url,
        string temporary,
        IProgress<GalleryDlDownloadUpdate>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(temporary);
        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            progress?.Report(new GalleryDlDownloadUpdate(received, total, "Lade gallery-dl …"));
        }
    }
}
