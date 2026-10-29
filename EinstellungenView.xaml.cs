using System.Collections.ObjectModel;

namespace MangaLibraryApp;

public partial class EinstellungenView
{
    private readonly ObservableCollection<string> _tags = new();
    private LibraryRepository? _library;

    public EinstellungenView()
    {
        InitializeComponent();
        TagList.ItemsSource = _tags;
    }

    public void Attach(LibraryRepository library)
    {
        _library = library;
        _tags.Clear();
        foreach (var tag in library.GetBlacklistedTags())
            _tags.Add(tag);
    }

    private void Add_Click(object sender, System.Windows.RoutedEventArgs e) => AddCurrent();

    private void TagBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
            return;
        e.Handled = true;
        AddCurrent();
    }

    private void Remove_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_library is null || (sender as System.Windows.FrameworkElement)?.DataContext is not string tag)
            return;
        _library.RemoveBlacklistedTag(tag);
        _tags.Remove(tag);
    }

    private void AddCurrent()
    {
        if (_library is null)
            return;
        var tag = TagBox.Text.Trim();
        if (tag.Length == 0 || _tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            return;
        _library.AddBlacklistedTag(tag);
        _tags.Add(tag);
        TagBox.Clear();
    }
}
