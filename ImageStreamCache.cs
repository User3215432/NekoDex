namespace MangaLibraryApp;

/// <summary>Lädt die nächsten Leserseiten vor, sobald die aktuelle Seite steht.</summary>
public static class ImageStreamCache
{
    public const int Ahead = 3;

    public static void Prefetch<T>(int index, int pageCount, Func<int, Task<T>> load, Action<Task<T>> observe)
    {
        for (var i = index + 1; i <= index + Ahead && i < pageCount; i++)
            observe(load(i));

        if (index > 0)
            observe(load(index - 1));
    }
}
