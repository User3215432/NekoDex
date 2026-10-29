using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace MangaLibraryApp;

/// <summary>Wie gallery-dl aufgerufen wird: Programm, vorangestellte Argumente (z. B. „-m gallery_dl“ bei Python) und eine Beschreibung.</summary>
internal sealed record GalleryDlCommand(string FileName, string[] PrefixArgs, string Description);

/// <summary>Eine Bild-Adresse einer Galerie; <see cref="Fallbacks"/> sind Ersatz-Adressen, die gallery-dl mit „| …“ nachliefert.</summary>
internal sealed class GalleryPage
{
    public GalleryPage(string url) => Url = url;

    public string Url { get; }
    public List<string> Fallbacks { get; } = new();
}

internal sealed class GalleryDlException : Exception
{
    public GalleryDlException(string message) : base(message)
    {
    }
}

/// <summary>Gemeinsame gallery-dl-Hilfen von Downloads (Hauptfenster) und Reader: Programm finden, starten, beenden, Bild-URLs auflisten.</summary>
internal static class GalleryDl
{
    // ────────────────────────────────────────────────────────────────────
    //  Programm finden und starten
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Beendet den Prozess samt Kindprozessen (die gallery-dl.exe startet unter Umständen einen Python-Unterprozess).</summary>
    public static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Prozess war bereits beendet
        }
    }

    public static ProcessStartInfo CreateStartInfo(GalleryDlCommand command, string? workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            info.WorkingDirectory = workingDirectory;

        foreach (var argument in command.PrefixArgs)
            info.ArgumentList.Add(argument);

        // Python schreibt in Pipes sonst in der ANSI-Codepage – Umlaute und japanische Titel kämen verstümmelt an.
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUNBUFFERED"] = "1"; // Zeilen sofort liefern (Fortschritt, Seiten des Readers)
        return info;
    }

    /// <summary>
    /// Reihenfolge: gespeicherter Pfad → gallery-dl.exe im App-Ordner → PATH → „py -m gallery_dl“ → „python -m gallery_dl“.
    /// Jeder Kandidat wird mit <c>--version</c> geprüft. Bei Misserfolg steht der Grund im zweiten Wert.
    /// </summary>
    public static async Task<(GalleryDlCommand? Command, string? Problem)> ResolveAsync(string? configuredPath)
    {
        var configured = (configuredPath ?? string.Empty).Trim().Trim('"');
        var candidates = new List<(string FileName, string[] Prefix)>();

        if (configured.Length > 0)
        {
            var file = Directory.Exists(configured) ? Path.Combine(configured, "gallery-dl.exe") : configured;
            if (!File.Exists(file))
                return (null, $"Datei nicht gefunden: {file}.");
            candidates.Add((file, Array.Empty<string>()));
        }
        else
        {
            var local = Path.Combine(AppContext.BaseDirectory, "gallery-dl.exe");
            if (File.Exists(local))
                candidates.Add((local, Array.Empty<string>()));

            var tools = GalleryDlService.ToolsPath;
            if (File.Exists(tools))
                candidates.Add((tools, Array.Empty<string>()));

            var bundled = GalleryDlService.BundledPath;
            if (File.Exists(bundled) && !string.Equals(bundled, tools, StringComparison.OrdinalIgnoreCase))
                candidates.Add((bundled, Array.Empty<string>()));

            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in pathDirs)
            {
                try
                {
                    var file = Path.Combine(dir.Trim('"'), "gallery-dl.exe");
                    if (File.Exists(file))
                        candidates.Add((file, Array.Empty<string>()));
                }
                catch (ArgumentException)
                {
                    // ungültiger PATH-Eintrag
                }
            }

            candidates.Add(("py", new[] { "-m", "gallery_dl" }));
            candidates.Add(("python", new[] { "-m", "gallery_dl" }));
        }

        foreach (var (fileName, prefix) in candidates)
        {
            var version = await ProbeAsync(fileName, prefix);
            if (version is null)
                continue;

            var how = prefix.Length > 0 ? $"{fileName} {string.Join(' ', prefix)}" : fileName;
            return (new GalleryDlCommand(fileName, prefix, $"gallery-dl {version}  ·  {how}"), null);
        }

        return (null, configured.Length > 0
            ? "Die Datei lässt sich nicht als gallery-dl ausführen (--version schlug fehl)."
            : "Weder gallery-dl.exe (App-Ordner, PATH) noch „python -m gallery_dl“ ist verfügbar.");
    }

    private static async Task<string?> ProbeAsync(string fileName, string[] prefix)
    {
        try
        {
            var info = CreateStartInfo(new GalleryDlCommand(fileName, prefix, string.Empty), null);
            info.ArgumentList.Add("--version");

            using var process = Process.Start(info);
            if (process is null)
                return null;

            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync(); // mitlesen, sonst kann der Prozess bei vollem Puffer blockieren

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                KillProcess(process);
                await DrainAsync(stdoutTask, stderrTask);
                return null;
            }

            var output = await stdoutTask;
            await stderrTask;
            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return process.ExitCode == 0 ? first : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null; // Programm nicht vorhanden / nicht startbar
        }
    }

    private static async Task DrainAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Prozess wurde beendet – Rest der Pipes ignorieren
        }
    }

    /// <summary>Zerlegt eine Argumentzeile; Anführungszeichen fassen Wörter zusammen (<c>--filter "a &gt;= 1"</c>).</summary>
    public static List<string> SplitArguments(string? text)
    {
        var arguments = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return arguments;

        var current = new StringBuilder();
        var inQuotes = false;
        var started = false;

        foreach (var ch in text)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (started)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(ch);
                started = true;
            }
        }

        if (started)
            arguments.Add(current.ToString());
        return arguments;
    }

    public static readonly string[] SessionCookieNames = { "ipb_member_id", "ipb_pass_hash", "cf_clearance" };

    public const string BrowserCookieLockedMessage =
        "Zugriff auf Browser-Cookies blockiert. Bitte schließe deinen Browser kurz, damit NekoDex die Session-Cookies lesen kann.";

    public const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    /// <summary><c>auto</c>, <c>chrome</c>, <c>edge</c>, <c>firefox</c>, <c>brave</c> oder <c>vivaldi</c>.</summary>
    public static string PreferredBrowser { get; set; } = "auto";

    /// <summary>
    /// Hängt <c>--cookies-from-browser</c> und den Chrome-User-Agent an.
    /// Bei <c>auto</c> wird <see cref="DetectInstalledBrowser"/> verwendet.
    /// </summary>
    public static void AddBrowserSession(ProcessStartInfo info, string? preferredBrowser = null)
    {
        var browser = ResolveBrowser(preferredBrowser ?? PreferredBrowser);
        var already = false;
        foreach (var argument in info.ArgumentList)
        {
            if (argument.Equals("--cookies-from-browser", StringComparison.OrdinalIgnoreCase))
            {
                already = true;
                break;
            }
        }

        var hasCookieFile = false;
        var hasNamedCookies = false;
        foreach (var argument in info.ArgumentList)
        {
            if (argument.Equals("--cookies", StringComparison.OrdinalIgnoreCase))
                hasCookieFile = true;
            if (argument.StartsWith("cookies.", StringComparison.OrdinalIgnoreCase))
                hasNamedCookies = true;
        }

        // --cookies-from-browser setzt dieselbe Option wie --cookies.
        // -o cookies.name danach indiziert einen String oder ein Tupel und gallery-dl bricht mit TypeError ab.
        if (!already && !hasCookieFile && !hasNamedCookies && browser.Length > 0)
        {
            info.ArgumentList.Add("--cookies-from-browser");
            info.ArgumentList.Add(browser);
        }

        info.ArgumentList.Add("-o");
        info.ArgumentList.Add("headers.User-Agent=" + ChromeUserAgent);
    }

    public static string ResolveBrowser(string? preferred)
    {
        var choice = (preferred ?? "auto").Trim().ToLowerInvariant();
        if (choice is "chrome" or "edge" or "firefox" or "brave" or "vivaldi")
            return choice;
        return DetectInstalledBrowser() ?? string.Empty;
    }

    /// <summary>
    /// Erster Browser mit Profilordner. Der Windows-Standardbrowser gewinnt, wenn sein Profil existiert.
    /// </summary>
    public static string? DetectInstalledBrowser()
    {
        var standard = ReadDefaultBrowser();
        if (standard is not null && ProfileExists(standard))
            return standard;

        foreach (var name in new[] { "chrome", "edge", "firefox", "brave", "vivaldi" })
        {
            if (ProfileExists(name))
                return name;
        }

        return null;
    }

    public static bool IsCookieDatabaseLocked(int exitCode, string? stderr)
    {
        var text = stderr ?? string.Empty;
        if (text.Contains("database is locked", StringComparison.OrdinalIgnoreCase))
            return true;
        return exitCode != 0
            && (text.Contains("locked", StringComparison.OrdinalIgnoreCase)
                || text.Contains("cookie", StringComparison.OrdinalIgnoreCase) && text.Contains("browser", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ProfileExists(string browser)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = browser switch
        {
            "chrome" => Path.Combine(local, "Google", "Chrome", "User Data"),
            "edge" => Path.Combine(local, "Microsoft", "Edge", "User Data"),
            "firefox" => Path.Combine(roaming, "Mozilla", "Firefox"),
            "brave" => Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
            "vivaldi" => Path.Combine(local, "Vivaldi", "User Data"),
            _ => string.Empty,
        };
        return path.Length > 0 && Directory.Exists(path);
    }

    private static string? ReadDefaultBrowser()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = key?.GetValue("ProgId") as string;
            if (string.IsNullOrWhiteSpace(progId))
                return null;
            if (progId.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
                return "chrome";
            if (progId.Contains("Edge", StringComparison.OrdinalIgnoreCase))
                return "edge";
            if (progId.Contains("Firefox", StringComparison.OrdinalIgnoreCase))
                return "firefox";
            if (progId.Contains("Brave", StringComparison.OrdinalIgnoreCase))
                return "brave";
            if (progId.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase))
                return "vivaldi";
            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Hängt <c>--cookies</c> und <c>--option cookies.name=wert</c> an.
    /// gallery-dl 1.32 kennt <c>--cookies</c>, <c>--cookies-from-browser</c> und <c>--option</c>.
    /// Die benannten Werte sind die Session, die der Nutzer in den Einstellungen hinterlegt.
    /// </summary>
    public static void AddCookieArguments(
        ProcessStartInfo info,
        string? cookieFile,
        IEnumerable<KeyValuePair<string, string>>? namedCookies)
    {
        if (!string.IsNullOrWhiteSpace(cookieFile) && File.Exists(cookieFile))
        {
            info.ArgumentList.Add("--cookies");
            info.ArgumentList.Add(cookieFile);
            return;
        }

        if (namedCookies is null)
            return;

        foreach (var pair in namedCookies)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;
            if (!SessionCookieNames.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                continue;
            info.ArgumentList.Add("--option");
            info.ArgumentList.Add("cookies." + pair.Key + "=" + pair.Value.Trim());
        }
    }

    public static string DescribeCommand(ProcessStartInfo info)
    {
        static string Quote(string value)
        {
            var shown = value;
            const string prefix = "cookies.";
            if (shown.StartsWith(prefix, StringComparison.Ordinal) && shown.Contains('='))
                shown = shown[..(shown.IndexOf('=') + 1)] + "***";
            return shown.Length == 0 || shown.Any(char.IsWhiteSpace) ? $"\"{shown}\"" : shown;
        }

        return string.Join(' ', new[] { info.FileName }.Concat(info.ArgumentList).Select(Quote));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Bild-URLs einer Galerie (für den Online-Reader)
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ruft <c>gallery-dl -g URL</c> auf und liefert die direkten Bild-Adressen <b>sobald sie eintreffen</b> – der Reader kann die erste Seite
    /// zeigen, während die übrigen noch gesucht werden. Für E-Hentai/ExHentai wird <c>-G</c> verwendet: Dort sind die Adressen der
    /// Bildseiten nur Zwischenschritte, erst <c>-G</c> löst sie zu den echten Bild-Adressen auf (je Seite eine Anfrage, daher langsamer).
    /// </summary>
    /// <param name="onWarning">Wird am Ende mit der letzten Fehlermeldung aufgerufen, falls gallery-dl nach einigen Seiten mit einem Fehler endete.</param>
    /// <exception cref="GalleryDlException">gallery-dl lieferte keine einzige Adresse.</exception>
    /// <summary>
    /// Lo-Fi-Galerien liegen unter <c>/lofi/g/</c>. gallery-dl erkennt nur <c>https://e-hentai.org/g/{id}/{token}/</c>.
    /// </summary>
    public static string ExtractorUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var host = uri.Host;
        if (!host.Equals("e-hentai.org", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".e-hentai.org", StringComparison.OrdinalIgnoreCase))
            return url;

        const string marker = "/lofi/g/";
        var index = uri.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return url;

        var path = "/g/" + uri.AbsolutePath[(index + marker.Length)..];
        return "https://e-hentai.org" + path + uri.Query;
    }

    public static async IAsyncEnumerable<GalleryPage> ListPagesAsync(
        GalleryDlCommand command,
        string url,
        string? extraArguments,
        string? workingDirectory,
        Action<string>? onWarning = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        url = ExtractorUrl(url);
        var site = SourceCatalog.FromUrl(url);
        var startInfo = CreateStartInfo(command, workingDirectory);
        startInfo.ArgumentList.Add(site is "ehentai" or "exhentai" ? "-G" : "-g");
        foreach (var argument in SplitArguments(extraArguments))
            startInfo.ArgumentList.Add(argument);
        AddBrowserSession(startInfo);
        startInfo.ArgumentList.Add(url);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new GalleryDlException("gallery-dl konnte nicht gestartet werden: " + ex.Message);
        }

        process.StandardInput.Close();
        using var cancelRegistration = cancellationToken.Register(() => KillProcess(process));

        // stderr parallel lesen (sonst kann der Prozess bei vollem Puffer blockieren) und die letzte Fehlerzeile merken.
        string? lastError = null;
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                {
                    if (line.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("[error]", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                        lastError = line.Trim();
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None);

        var count = 0;
        GalleryPage? last = null;
        string? output;
        while ((output = await process.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
        {
            var line = output.Trim();
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                last = new GalleryPage(line);
                count++;
                yield return last;
            }
            else if (line.StartsWith('|') && last is not null)
            {
                // „| url“ – Ersatz-Adresse zur vorherigen Seite
                var fallback = line[1..].Trim();
                if (fallback.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    last.Fallbacks.Add(fallback);
            }
        }

        await process.WaitForExitAsync(cancellationToken);
        try
        {
            await stderrTask;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }

        if (process.ExitCode != 0)
        {
            var message = IsCookieDatabaseLocked(process.ExitCode, lastError)
                ? BrowserCookieLockedMessage
                : lastError ?? $"gallery-dl wurde mit Code {process.ExitCode} beendet.";
            if (count == 0)
                throw new GalleryDlException(message);

            onWarning?.Invoke(message);
        }
        else if (count == 0)
        {
            throw new GalleryDlException(lastError ?? "gallery-dl hat keine Bild-Adressen geliefert – wird diese Adresse unterstützt?");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Online-Suche: JSON-Dump der Galerien (ohne Dateien zu speichern)
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ruft <c>gallery-dl -j --range 1 URL</c> auf und liefert den JSON-Text.
    /// <c>-j</c> ist <c>--dump-json</c> (gallery-dl hat keinen Schalter <c>--json</c> und kein <c>--page-range</c>).
    /// <c>--range 1</c> nimmt nur die erste Datei jeder Galerie. Die Ergebnisseite steht in der URL.
    /// </summary>
    public static async Task<string> DumpJsonAsync(
        GalleryDlCommand command,
        string url,
        string? extraArguments,
        string? workingDirectory,
        CancellationToken cancellationToken = default,
        string? cookieFile = null,
        string pageRange = "1",
        IReadOnlyList<string>? leadingArguments = null,
        IEnumerable<KeyValuePair<string, string>>? namedCookies = null)
    {
        var startInfo = CreateStartInfo(command, workingDirectory);
        startInfo.ArgumentList.Add("-j");
        startInfo.ArgumentList.Add("--range");
        startInfo.ArgumentList.Add("1");
        // Diese gallery-dl-Fassung kennt kein --page-range (argparse beendet dann mit Code 2).
        // Die Ergebnisseite steht in der URL (?page=).
        _ = pageRange;
        AddCookieArguments(startInfo, cookieFile, namedCookies);
        AddBrowserSession(startInfo);

        if (leadingArguments is not null)
        {
            foreach (var argument in leadingArguments)
                startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in SplitArguments(extraArguments))
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(ExtractorUrl(url));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new GalleryDlException("gallery-dl konnte nicht gestartet werden: " + ex.Message);
        }

        process.StandardInput.Close();
        using var cancelRegistration = cancellationToken.Register(() => KillProcess(process));

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw new GalleryDlException(cancellationToken.IsCancellationRequested
                ? "Suche abgebrochen."
                : "Die Online-Suche hat zu lange gedauert (90 s).");
        }

        string stdout;
        string stderr;
        try
        {
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new GalleryDlException("Keine Antwort von gallery-dl: " + ex.Message);
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            if (IsCookieDatabaseLocked(process.ExitCode, stderr))
                throw new GalleryDlException(BrowserCookieLockedMessage);

            var error = LastErrorLine(stderr) ?? (process.ExitCode == 0
                ? "gallery-dl hat keine JSON-Daten geliefert – wird diese Suche unterstützt?"
                : $"gallery-dl wurde mit Code {process.ExitCode} beendet.");
            throw new GalleryDlException(error);
        }

        return stdout;
    }

    private static string? LastErrorLine(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return null;

        string? last = null;
        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Contains("[error]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error:", StringComparison.OrdinalIgnoreCase)
                || line.Contains("unrecognized arguments", StringComparison.OrdinalIgnoreCase)
                || line.Contains("TypeError", StringComparison.Ordinal))
                last = line;
        }

        return last;
    }
}
