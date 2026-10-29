using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;

namespace MangaLibraryApp;

/// <summary>
/// Hält Cover im Arbeitsspeicher und, bei Remote-URLs, zusätzlich auf der Platte,
/// damit virtualisierte Raster dieselben Bilder nicht erneut dekodieren.
/// </summary>
internal static class ImageCacheService
{
    private const int MemoryLimit = 96;
    private const int DiskFileLimit = 400;
    private const int DecodeWidth = 360;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<CacheEntry> Order = new();
    private static readonly Dictionary<string, Task<ImageSource?>> Pending = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string DiskDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MangaLibraryApp",
        "image-cache");

    public static bool TryGet(string? key, out ImageSource? image)
    {
        image = null;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        lock (Gate)
        {
            if (!Memory.TryGetValue(key, out var node))
                return false;

            Order.Remove(node);
            Order.AddFirst(node);
            image = node.Value.Image;
            return true;
        }
    }

    public static Task<ImageSource?> GetFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult<ImageSource?>(null);
        if (TryGet(path, out var cached))
            return Task.FromResult(cached);

        _ = cancellationToken;
        return Share(path, _ => LoadFileAsync(path));
    }

    public static Task<ImageSource?> GetRemoteAsync(string url, string? source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Task.FromResult<ImageSource?>(null);
        if (TryGet(url, out var cached))
            return Task.FromResult(cached);

        _ = cancellationToken;
        return Share(url, token => LoadRemoteAsync(url, source, token));
    }

    private static Task<ImageSource?> Share(string key, Func<CancellationToken, Task<ImageSource?>> load)
    {
        lock (Gate)
        {
            if (Memory.TryGetValue(key, out var node))
            {
                Order.Remove(node);
                Order.AddFirst(node);
                return Task.FromResult<ImageSource?>(node.Value.Image);
            }

            if (Pending.TryGetValue(key, out var existing))
                return existing;

            var created = load(CancellationToken.None);
            Pending[key] = created;
            _ = created.ContinueWith(
                _ =>
                {
                    lock (Gate)
                        Pending.Remove(key);
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return created;
        }
    }

    private static async Task<ImageSource?> LoadFileAsync(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var image = CoverImageLoader.Decode(bytes, DecodeWidth);
            if (image is not null)
                Remember(path, image);
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<ImageSource?> LoadRemoteAsync(string url, string? source, CancellationToken cancellationToken)
    {
        var disk = DiskPath(url);
        if (File.Exists(disk))
        {
            try
            {
                var cachedBytes = await File.ReadAllBytesAsync(disk, cancellationToken).ConfigureAwait(false);
                var cachedImage = CoverImageLoader.Decode(cachedBytes, DecodeWidth);
                if (cachedImage is not null)
                {
                    Remember(url, cachedImage);
                    return cachedImage;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            try { File.Delete(disk); }
            catch (IOException) { }
        }

        var bytes = await CoverImageLoader.DownloadBytesAsync(url, source, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return null;

        try
        {
            Directory.CreateDirectory(DiskDirectory);
            var temporary = disk + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, disk, overwrite: true);
            TrimDisk();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var image = CoverImageLoader.Decode(bytes, DecodeWidth);
        if (image is not null)
            Remember(url, image);
        return image;
    }

    private static void Remember(string key, ImageSource image)
    {
        lock (Gate)
        {
            if (Memory.TryGetValue(key, out var existing))
            {
                existing.Value.Image = image;
                Order.Remove(existing);
                Order.AddFirst(existing);
                return;
            }

            var node = Order.AddFirst(new CacheEntry(key, image));
            Memory[key] = node;
            while (Order.Count > MemoryLimit)
            {
                var last = Order.Last;
                if (last is null)
                    break;
                Order.RemoveLast();
                Memory.Remove(last.Value.Key);
            }
        }
    }

    private static string DiskPath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(DiskDirectory, hash + ".img");
    }

    private static void TrimDisk()
    {
        try
        {
            var files = new DirectoryInfo(DiskDirectory)
                .EnumerateFiles("*.img")
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();
            for (var index = 0; index < files.Count - DiskFileLimit; index++)
            {
                try { files[index].Delete(); }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(string key, ImageSource image)
        {
            Key = key;
            Image = image;
        }

        public string Key { get; }
        public ImageSource Image { get; set; }
    }
}
