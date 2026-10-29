using System.Text.RegularExpressions;

namespace MangaLibraryApp;

/// <summary>Liest Fortschritt, Geschwindigkeit und frühe Abbrüche aus einer gallery-dl-Zeile.</summary>
public static class DownloadsViewModel
{
    private static readonly Regex Fraction = new(@"\[?\b(\d+)\s*/\s*(\d+)\]?", RegexOptions.Compiled);
    private static readonly Regex Percent = new(@"(\d+(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex Speed = new(@"(\d+(?:[.,]\d+)?)\s*([kKmMgGtT]?i?B)/s", RegexOptions.Compiled);

    public static void Apply(DownloadJob job, string line)
    {
        var fraction = Fraction.Match(line);
        if (fraction.Success
            && int.TryParse(fraction.Groups[1].Value, out var current)
            && int.TryParse(fraction.Groups[2].Value, out var total)
            && total > 0
            && current <= total)
        {
            job.SetProgress(current, total, ReadSpeed(line));
        }
        else if (Percent.Match(line) is { Success: true } percent
                 && double.TryParse(percent.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            job.SetPercent(value, ReadSpeed(line));
        }
        else if (ReadSpeed(line) is { Length: > 0 } speed && job.ProgressTotal > 0)
        {
            job.SetProgress(job.ProgressCurrent, job.ProgressTotal, speed);
        }

        if (IsEarlyFailure(line))
        {
            job.LastError = line.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
                || line.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                ? LanguageManager.Instance.Get("Web_CookieLocked")
                : LanguageManager.Instance.Get("Web_Cloudflare");
            job.MarkEarlyFault(job.LastError);
        }
    }

    public static bool IsEarlyFailure(string line) =>
        line.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
        || line.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase)
        || line.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase)
        || line.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)
        || line.Contains("No cookies", StringComparison.OrdinalIgnoreCase);

    private static string? ReadSpeed(string line)
    {
        var match = Speed.Match(line);
        return match.Success ? match.Groups[1].Value + " " + match.Groups[2].Value + "/s" : null;
    }
}
