using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MangaLibraryApp;

/// <summary>
/// Lädt Kachel-Cover mit Chrome-User-Agent und seitenrichtigem Referer.
/// Hitomi blockiert <c>tn</c>-Bilder ohne <c>Referer: https://hitomi.la/</c>.
/// </summary>
internal static class CoverImageLoader
{
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const long MaxCoverBytes = 8L * 1024 * 1024;
    private const int DecodeWidth = 360;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Gate = new(4, 4);
    private static readonly SemaphoreSlim GgGate = new(1, 1);
    private static readonly Regex Hash64 = new(@"[0-9a-fA-F]{64}", RegexOptions.Compiled);
    private static readonly Regex GgCase = new(@"case\s+(\d+)\s*:", RegexOptions.Compiled);
    private static HashSet<int>? _ggOnes;

    public static async Task<ImageSource?> LoadAsync(string url, string? source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (IsHitomi(source, url))
            await EnsureGgAsync(cancellationToken);

        foreach (var candidate in Candidates(url, source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = await TryLoadAsync(candidate, RefererFor(source, candidate), cancellationToken);
            if (image is not null)
                return image;
        }

        return null;
    }

    private static bool IsHitomi(string? source, string url) =>
        string.Equals(source, "hitomi", StringComparison.OrdinalIgnoreCase)
        || url.Contains("hitomi.la", StringComparison.OrdinalIgnoreCase)
        || url.Contains("gold-usergeneratedcontent.net", StringComparison.OrdinalIgnoreCase);

    private static string RefererFor(string? source, string url)
    {
        if (IsHitomi(source, url))
            return "https://hitomi.la/";
        if (string.Equals(source, "pururin", StringComparison.OrdinalIgnoreCase) || url.Contains("pururin.", StringComparison.OrdinalIgnoreCase))
            return "https://pururin.to/";
        if (string.Equals(source, "hbrowse", StringComparison.OrdinalIgnoreCase) || url.Contains("hbrowse.com", StringComparison.OrdinalIgnoreCase))
            return "https://www.hbrowse.com/";
        if (SourceCatalog.Find(source)?.Root is { Length: > 0 } root)
            return root;
        return "https://hitomi.la/";
    }

    private static IEnumerable<string> Candidates(string url, string? source)
    {
        var seen = new List<string>();
        var guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && guard.Add(candidate))
                seen.Add(candidate);
        }

        if (IsHitomi(source, url))
        {
            var hash = Hash64.Match(url).Value.ToLowerInvariant();
            if (hash.Length == 64)
            {
                var last = hash[^1];
                var two = hash.Substring(hash.Length - 3, 2);
                var g = Convert.ToInt32(string.Concat(last, two), 16);
                var primary = _ggOnes is not null && _ggOnes.Contains(g) ? "b" : "a";
                var other = primary == "a" ? "b" : "a";
                var path = $"{last}/{two}/{hash}";
                // Live-CDN: smallbigtn/*.jpg antwortet 404, webpsmalltn/*.webp liefert das Cover.
                // tn.hitomi.la löst sich nicht auf.
                Add($"https://{primary}tn.gold-usergeneratedcontent.net/webpsmalltn/{path}.webp");
                Add($"https://{primary}tn.gold-usergeneratedcontent.net/webpbigtn/{path}.webp");
                Add($"https://{other}tn.gold-usergeneratedcontent.net/webpsmalltn/{path}.webp");
                Add($"https://{other}tn.gold-usergeneratedcontent.net/webpbigtn/{path}.webp");
                Add($"https://{primary}tn.gold-usergeneratedcontent.net/smallbigtn/{path}.webp");
                Add($"https://{primary}tn.gold-usergeneratedcontent.net/smallbigtn/{path}.jpg");
            }
        }

        Add(url);
        return seen;
    }

    private static async Task EnsureGgAsync(CancellationToken ct)
    {
        if (_ggOnes is not null)
            return;

        await GgGate.WaitAsync(ct);
        try
        {
            if (_ggOnes is not null)
                return;

            var set = new HashSet<int>();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://ltn.gold-usergeneratedcontent.net/gg.js");
                request.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
                request.Headers.TryAddWithoutValidation("Referer", "https://hitomi.la/");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(ct);
                    foreach (Match match in GgCase.Matches(text))
                    {
                        if (int.TryParse(match.Groups[1].Value, out var n))
                            set.Add(n);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested)
                    throw;
            }

            _ggOnes = set;
        }
        finally
        {
            GgGate.Release();
        }
    }

    private static async Task<ImageSource?> TryLoadAsync(string url, string referer, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            request.Headers.TryAddWithoutValidation("Referer", referer);
            request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var total = response.Content.Headers.ContentLength;
            if (total > MaxCoverBytes)
                return null;

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(total is > 0 and <= int.MaxValue ? (int)total.Value : 64 * 1024);
            await body.CopyToAsync(buffer, ct);
            if (buffer.Length is 0 or > MaxCoverBytes)
                return null;

            var bytes = buffer.ToArray();
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
                return Decode(bytes);
            if (dispatcher.CheckAccess())
                return Decode(bytes);
            return await dispatcher.InvokeAsync(() => Decode(bytes));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or NotSupportedException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = DecodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(12),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
    }
}
