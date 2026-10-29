using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MangaLibraryApp;

public partial class BibliothekView
{
    private readonly LibraryViewModel _model = new();
    private readonly DispatcherTimer _pageTimer;
    private int? _pageMin;
    private int? _pageMax;

    public BibliothekView()
    {
        _model.DeleteInvoked += (_, _) => DeleteSelected();
        InitializeComponent();
        DataContext = _model;
        _pageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _pageTimer.Tick += (_, _) =>
        {
            _pageTimer.Stop();
            PublishPageRange();
        };
    }

    public event EventHandler<LocalManga>? OpenRequested;
    public event EventHandler<LocalManga>? FavoriteToggled;
    public event EventHandler<ReadingStatus?>? ReadingFilterChanged;
    public event EventHandler<LocalManga>? ReadingStatusChanged;
    public event EventHandler<LibraryDeleteRequest>? DeleteRequested;
    public event EventHandler<LibraryCategoryRequest>? CategoryAssignRequested;
    public event EventHandler<string?>? CategoryFilterChanged;
    public event EventHandler<string>? CategoryCreated;
    public event EventHandler<LibraryCategoryRename>? CategoryRenamed;
    public event EventHandler<string>? CategoryDeleted;
    public event EventHandler<LibraryPageRange>? PageRangeChanged;

    private static readonly string[] SuggestedCategories = ["Plan to Read", "Klassiker", "Gelesen"];

    public void Show(IEnumerable<LocalManga> items) => _model.Replace(items);

    public void SetCategories(IEnumerable<string> names, string? selected) => _model.SetCategories(names, selected);

    public bool Remove(long id) => _model.Remove(id);

    public void UpdateStatus(long id, ReadingStatus status) => _model.UpdateStatus(id, status);

    public void Card_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.DependencyObject source && FindParent<System.Windows.Controls.Button>(source) is not null)
            return;
        if ((sender as System.Windows.FrameworkElement)?.DataContext is not LocalManga manga)
            return;
        if (_model.SelectionMode)
        {
            manga.IsSelected = !manga.IsSelected;
            e.Handled = true;
            return;
        }

        OpenRequested?.Invoke(this, manga);
    }

    private void SelectionMode_Click(object sender, System.Windows.RoutedEventArgs e) =>
        _model.SelectionMode = !_model.SelectionMode;

    private void SelectAll_Click(object sender, System.Windows.RoutedEventArgs e) => _model.SelectAllVisible();

    private void ClearSelection_Click(object sender, System.Windows.RoutedEventArgs e) => _model.ClearSelection();

    private void Selection_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as System.Windows.FrameworkElement)?.DataContext is LocalManga manga)
            manga.IsSelected = !manga.IsSelected;
    }

    private void DeleteSelected()
    {
        var items = _model.SelectedItems();
        if (items.Count == 0)
            return;
        if (ConfirmDelete(items.Count) is not { } deleteFiles)
            return;
        DeleteRequested?.Invoke(this, new LibraryDeleteRequest(items, deleteFiles));
    }

    private void DeleteOne_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (MangaFromMenu(sender) is not { } manga)
            return;
        if (ConfirmDelete(1) is not { } deleteFiles)
            return;
        DeleteRequested?.Invoke(this, new LibraryDeleteRequest(new[] { manga }, deleteFiles));
    }

    private void AssignSelected_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var items = _model.SelectedItems();
        if (items.Count == 0)
        {
            System.Windows.MessageBox.Show(
                System.Windows.Window.GetWindow(this),
                "Bitte zuerst Titel auswählen.",
                "Kategorie zuweisen",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        Assign(items);
    }

    private void AssignOne_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (MangaFromMenu(sender) is not { } manga)
            return;
        var items = manga.IsSelected ? _model.SelectedItems() : new[] { manga };
        Assign(items);
    }

    private void CategoryChip_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.DataContext is not CategoryChip chip)
            return;
        foreach (var item in _model.Categories)
            item.IsSelected = ReferenceEquals(item, chip);
        CategoryFilterChanged?.Invoke(this, chip.Filter);
    }

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptText("Neue Kategorie", "Name der Kategorie", "Anlegen", null);
        if (string.IsNullOrWhiteSpace(name))
            return;
        CategoryCreated?.Invoke(this, name.Trim());
    }

    private void RenameCategory_Click(object sender, RoutedEventArgs e)
    {
        if (ChipFromMenu(sender) is not { Filter: { } name })
            return;
        var next = PromptText("Kategorie umbenennen", "Neuer Name", "Speichern", name);
        if (string.IsNullOrWhiteSpace(next) || string.Equals(next.Trim(), name, StringComparison.OrdinalIgnoreCase))
            return;
        CategoryRenamed?.Invoke(this, new LibraryCategoryRename(name, next.Trim()));
    }

    private void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (ChipFromMenu(sender) is not { Filter: { } name })
            return;
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Kategorie „{name}“ löschen?\n\nDie Zuordnungen werden entfernt. Die Titel bleiben in der Bibliothek.",
            "Kategorie löschen",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
            return;
        CategoryDeleted?.Invoke(this, name);
    }

    private void CategoryMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;
        var enabled = ChipFromMenu(menu) is { Filter: not null };
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CardMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;
        var assign = menu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "assign"));
        if (assign is null)
            return;

        assign.Items.Clear();
        foreach (var name in _model.CategoryNames())
        {
            var item = new MenuItem { Header = name, Tag = name };
            item.Click += AssignNamed_Click;
            assign.Items.Add(item);
        }

        if (assign.Items.Count > 0)
            assign.Items.Add(new Separator());
        var create = new MenuItem { Header = "Neue Kategorie…", Tag = "new" };
        create.Click += AssignOne_Click;
        assign.Items.Add(create);
    }

    private void AssignNamed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string name } || name.Length == 0)
            return;
        if (MangaFromMenu(sender) is not { } manga)
            return;
        var items = manga.IsSelected ? _model.SelectedItems() : new[] { manga };
        if (items.Count == 0)
            items = new[] { manga };
        CategoryAssignRequested?.Invoke(this, new LibraryCategoryRequest(items.Select(item => item.Id).ToList(), name));
    }

    private void PageRange_TextChanged(object sender, TextChangedEventArgs e)
    {
        _pageTimer.Stop();
        _pageTimer.Start();
    }

    private void PublishPageRange()
    {
        var min = ParsePages(PageMinBox.Text);
        var max = ParsePages(PageMaxBox.Text);
        if (min == _pageMin && max == _pageMax)
            return;
        _pageMin = min;
        _pageMax = max;
        PageRangeChanged?.Invoke(this, new LibraryPageRange(min, max));
    }

    private static int? ParsePages(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)
            || int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return value > 0 ? value : null;
        return null;
    }

    private string? PromptText(string title, string label, string confirm, string? initial)
    {
        var box = new TextBox
        {
            Text = initial ?? string.Empty,
            Width = 320,
            Margin = new Thickness(16, 8, 16, 8),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C)),
        };
        var ok = new Button
        {
            Content = confirm,
            Width = 110,
            Height = 32,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var cancel = new Button
        {
            Content = "Abbrechen",
            Width = 110,
            Height = 32,
            IsCancel = true,
        };
        string? result = null;
        Window dialog = null!;
        ok.Click += (_, _) =>
        {
            result = box.Text?.Trim();
            if (string.IsNullOrWhiteSpace(result))
                return;
            dialog.DialogResult = true;
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(16, 16, 16, 0),
            Foreground = Brushes.White,
        });
        root.Children.Add(box);
        root.Children.Add(buttons);

        dialog = new Window
        {
            Title = title,
            Content = root,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22)),
            Foreground = Brushes.White,
            Owner = Window.GetWindow(this),
        };
        return dialog.ShowDialog() == true ? result : null;
    }

    private static CategoryChip? ChipFromMenu(object sender)
    {
        var menu = sender as ContextMenu ?? MenuOf(sender);
        if (menu?.PlacementTarget is FrameworkElement { DataContext: CategoryChip chip })
            return chip;
        if (sender is FrameworkElement { DataContext: CategoryChip direct })
            return direct;
        return null;
    }

    private void Assign(IReadOnlyList<LocalManga> items)
    {
        var name = AskCategory(_model.CategoryNames());
        if (string.IsNullOrWhiteSpace(name))
            return;
        CategoryAssignRequested?.Invoke(this, new LibraryCategoryRequest(items.Select(item => item.Id).ToList(), name));
    }

    private bool? ConfirmDelete(int count)
    {
        var answer = System.Windows.MessageBox.Show(
            System.Windows.Window.GetWindow(this),
            $"Möchtest du {count} Titel wirklich löschen?\n\nJa entfernt auch die heruntergeladenen Dateien. Nein löscht nur den Bibliothekseintrag.",
            "Titel löschen",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Warning);
        return answer switch
        {
            System.Windows.MessageBoxResult.Yes => true,
            System.Windows.MessageBoxResult.No => false,
            _ => null,
        };
    }

    private string? AskCategory(IEnumerable<string> existing)
    {
        var names = SuggestedCategories.Concat(existing)
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var box = new System.Windows.Controls.ComboBox
        {
            IsEditable = true,
            Width = 320,
            Margin = new System.Windows.Thickness(16, 8, 16, 8),
            ItemsSource = names,
        };
        var ok = new System.Windows.Controls.Button
        {
            Content = "Zuweisen",
            Width = 110,
            Height = 32,
            IsDefault = true,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "Abbrechen",
            Width = 110,
            Height = 32,
            IsCancel = true,
        };
        string? result = null;
        System.Windows.Window dialog = null!;
        ok.Click += (_, _) =>
        {
            result = box.Text?.Trim();
            if (string.IsNullOrWhiteSpace(result))
                return;
            dialog.DialogResult = true;
        };

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new System.Windows.Thickness(16, 0, 16, 16),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new System.Windows.Controls.StackPanel();
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Name der Sammlung",
            Margin = new System.Windows.Thickness(16, 16, 16, 0),
            Foreground = System.Windows.Media.Brushes.White,
        });
        root.Children.Add(box);
        root.Children.Add(buttons);

        dialog = new System.Windows.Window
        {
            Title = "Kategorie zuweisen",
            Content = root,
            Width = 380,
            SizeToContent = System.Windows.SizeToContent.Height,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x19, 0x22)),
            Foreground = System.Windows.Media.Brushes.White,
            Owner = System.Windows.Window.GetWindow(this),
        };
        return dialog.ShowDialog() == true ? result : null;
    }

    private static LocalManga? MangaFromMenu(object sender)
    {
        var menu = MenuOf(sender);
        return menu?.PlacementTarget is FrameworkElement { DataContext: LocalManga manga } ? manga : null;
    }

    private static ContextMenu? MenuOf(object sender)
    {
        DependencyObject? current = sender as DependencyObject;
        while (current is not null)
        {
            if (current is ContextMenu menu)
                return menu;
            current = current is FrameworkElement element ? element.Parent : null;
        }

        return null;
    }

    private void StatusFilter_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string tag })
            return;

        ReadingStatus? status = tag switch
        {
            "Unread" => ReadingStatus.Unread,
            "Reading" => ReadingStatus.Reading,
            "Completed" => ReadingStatus.Completed,
            _ => null,
        };
        _model.ReadingFilter = status;
        ReadingFilterChanged?.Invoke(this, status);
    }

    private void ReadingStatus_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string tag, Parent: System.Windows.Controls.ContextMenu menu })
            return;
        if (menu.PlacementTarget is not System.Windows.FrameworkElement { DataContext: LocalManga manga })
            return;
        if (!Enum.TryParse<ReadingStatus>(tag, out var status) || manga.ReadingStatus == status)
            return;

        manga.ReadingStatus = status;
        ReadingStatusChanged?.Invoke(this, manga);
    }

    private void Favorite_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as System.Windows.FrameworkElement)?.DataContext is LocalManga manga)
            FavoriteToggled?.Invoke(this, manga);
    }

    private static T? FindParent<T>(System.Windows.DependencyObject source) where T : System.Windows.DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }

        return null;
    }
}

public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0)
            return null;

        return ImageCacheService.TryGet(path, out var image) ? image : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
