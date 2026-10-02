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

    public static string DescribeCommand(ProcessStartInfo info)
    {
        static string Quote(string value) => value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
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
    public static async IAsyncEnumerable<GalleryPage> ListPagesAsync(
        GalleryDlCommand command,
        string url,
        string? extraArguments,
        string? workingDirectory,
        Action<string>? onWarning = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var site = SourceCatalog.FromUrl(url);
        var startInfo = CreateStartInfo(command, workingDirectory);
        startInfo.ArgumentList.Add(site is "ehentai" or "exhentai" ? "-G" : "-g");
        foreach (var argument in SplitArguments(extraArguments))
            startInfo.ArgumentList.Add(argument);
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
                    if (line.Contains("[error]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
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
            var message = lastError ?? $"gallery-dl wurde mit Code {process.ExitCode} beendet.";
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
    /// Ruft <c>gallery-dl -j --range 1 --page-range PAGE URL</c> auf und liefert den JSON-Text.
    /// <c>-j</c> ist <c>--dump-json</c> (gallery-dl hat keinen Schalter <c>--json</c>). <c>--range 1</c> nimmt nur die
    /// erste Datei jeder Galerie. <paramref name="pageRange"/> wählt die Ergebnisseiten (z. B. <c>2</c> oder <c>1-5</c>).
    /// </summary>
    public static async Task<string> DumpJsonAsync(
        GalleryDlCommand command,
        string url,
        string? extraArguments,
        string? workingDirectory,
        CancellationToken cancellationToken = default,
        string? cookieFile = null,
        string pageRange = "1",
        IReadOnlyList<string>? leadingArguments = null)
    {
        var startInfo = CreateStartInfo(command, workingDirectory);
        startInfo.ArgumentList.Add("-j");
        startInfo.ArgumentList.Add("--range");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--page-range");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(pageRange) ? "1" : pageRange);
        if (!string.IsNullOrWhiteSpace(cookieFile) && File.Exists(cookieFile))
        {
            startInfo.ArgumentList.Add("--cookies");
            startInfo.ArgumentList.Add(cookieFile);
        }

        if (leadingArguments is not null)
        {
            foreach (var argument in leadingArguments)
                startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in SplitArguments(extraArguments))
            startInfo.ArgumentList.Add(argument);
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
            if (line.Contains("[error]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                last = line;
        }

        return last;
    }
}
