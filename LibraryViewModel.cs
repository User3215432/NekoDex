using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;

namespace MangaLibraryApp;

public enum LibrarySort
{
    LastRead,
    DateAdded,
    Title,
}

/// <summary>Cover-Raster der lokalen Bibliothek: Suche, Tag-Chips, Sortierung und Mehrfachauswahl.</summary>
public sealed class LibraryViewModel : ObservableObject
{
    private string _searchText = string.Empty;
    private LibrarySort _sort = LibrarySort.DateAdded;
    private ReadingStatus? _readingFilter;
    private bool _selectionMode;
    private bool _bulkSelection;

    public LibraryViewModel()
    {
        Categories.Add(new CategoryChip("Alle Kategorien", null) { IsSelected = true });
        DeleteCommand = new RelayCommand(() => DeleteInvoked?.Invoke(this, EventArgs.Empty), () => SelectedCount > 0);
    }

    public ObservableCollection<LocalManga> Items { get; } = new();
    public ObservableCollection<string> TagFilters { get; } = new();
    public ObservableCollection<CategoryChip> Categories { get; } = new();

    public RelayCommand DeleteCommand { get; }

    /// <summary>Die Ansicht zeigt den Bestätigungsdialog und löst danach das Löschen aus.</summary>
    public event EventHandler? DeleteInvoked;

    public string SearchText
    {
        get => _searchText;
        set => SetField(ref _searchText, value);
    }

    public LibrarySort Sort
    {
        get => _sort;
        set => SetField(ref _sort, value);
    }

    /// <summary><c>null</c> ist der Filter „Alle“.</summary>
    public ReadingStatus? ReadingFilter
    {
        get => _readingFilter;
        set => SetField(ref _readingFilter, value);
    }

    public bool SelectionMode
    {
        get => _selectionMode;
        set
        {
            if (!SetField(ref _selectionMode, value))
                return;
            if (!value)
            {
                _bulkSelection = true;
                try
                {
                    foreach (var item in Items)
                        item.IsSelected = false;
                }
                finally
                {
                    _bulkSelection = false;
                }
            }

            OnPropertyChanged(nameof(SelectionModeLabel));
            UpdateSelectionChrome();
        }
    }

    public string SelectionModeLabel => _selectionMode ? "Auswahl beenden" : "Mehrfachauswahl";

    public int SelectedCount => Items.Count(item => item.IsSelected);

    public bool UpdateStatus(long id, ReadingStatus status)
    {
        var item = Items.FirstOrDefault(entry => entry.Id == id);
        if (item is null)
            return false;
        item.ReadingStatus = status;
        if (_readingFilter is { } filter && filter != status)
            return Remove(id);
        return true;
    }

    public bool HasItems => Items.Count > 0;

    public void Replace(IEnumerable<LocalManga> items)
    {
        foreach (var old in Items)
            old.PropertyChanged -= OnItemPropertyChanged;
        Items.Clear();
        foreach (var item in items)
        {
            item.ShowSelection = _selectionMode;
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        OnPropertyChanged(nameof(HasItems));
        UpdateSelectionChrome();
    }

    public void SetTags(IEnumerable<string> tags)
    {
        TagFilters.Clear();
        foreach (var tag in tags)
            TagFilters.Add(tag);
    }

    public void SetCategories(IEnumerable<string> names, string? selected)
    {
        Categories.Clear();
        Categories.Add(new CategoryChip("Alle Kategorien", null) { IsSelected = string.IsNullOrEmpty(selected) });
        foreach (var name in names)
        {
            Categories.Add(new CategoryChip(name, name)
            {
                IsSelected = string.Equals(name, selected, StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    public IReadOnlyList<string> CategoryNames() =>
        Categories.Where(chip => chip.Filter is not null).Select(chip => chip.Filter!).ToList();

    public IReadOnlyList<LocalManga> SelectedItems() => Items.Where(item => item.IsSelected).ToList();

    public void SelectAllVisible()
    {
        if (!_selectionMode)
        {
            _selectionMode = true;
            OnPropertyChanged(nameof(SelectionMode));
            OnPropertyChanged(nameof(SelectionModeLabel));
        }

        _bulkSelection = true;
        try
        {
            foreach (var item in Items)
                item.IsSelected = true;
        }
        finally
        {
            _bulkSelection = false;
            UpdateSelectionChrome();
        }
    }

    public void ClearSelection()
    {
        _bulkSelection = true;
        try
        {
            foreach (var item in Items)
                item.IsSelected = false;
        }
        finally
        {
            _bulkSelection = false;
            UpdateSelectionChrome();
        }
    }

    public bool Remove(long id)
    {
        var item = Items.FirstOrDefault(entry => entry.Id == id);
        if (item is null)
            return false;
        item.PropertyChanged -= OnItemPropertyChanged;
        Items.Remove(item);
        OnPropertyChanged(nameof(HasItems));
        UpdateSelectionChrome();
        return true;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_bulkSelection && e.PropertyName == nameof(LocalManga.IsSelected))
            UpdateSelectionChrome();
    }

    private void UpdateSelectionChrome()
    {
        var show = _selectionMode || Items.Any(item => item.IsSelected);
        foreach (var item in Items)
            item.ShowSelection = show;
        OnPropertyChanged(nameof(SelectedCount));
        DeleteCommand.RaiseCanExecuteChanged();
    }
}

public sealed class CategoryChip : ObservableObject
{
    private bool _isSelected;

    public CategoryChip(string label, string? filter)
    {
        Label = label;
        Filter = filter;
    }

    public string Label { get; }

    /// <summary><c>null</c> hebt den Kategoriefilter auf.</summary>
    public string? Filter { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}

public sealed class LibraryDeleteRequest : EventArgs
{
    public LibraryDeleteRequest(IReadOnlyList<LocalManga> items, bool deleteFiles)
    {
        Items = items;
        DeleteFiles = deleteFiles;
    }

    public IReadOnlyList<LocalManga> Items { get; }
    public bool DeleteFiles { get; }
}

public sealed class LibraryCategoryRequest : EventArgs
{
    public LibraryCategoryRequest(IReadOnlyList<long> ids, string name)
    {
        Ids = ids;
        Name = name;
    }

    public IReadOnlyList<long> Ids { get; }
    public string Name { get; }
}

public sealed class LibraryCategoryRename : EventArgs
{
    public LibraryCategoryRename(string oldName, string newName)
    {
        OldName = oldName;
        NewName = newName;
    }

    public string OldName { get; }
    public string NewName { get; }
}

public sealed class LibraryPageRange : EventArgs
{
    public LibraryPageRange(int? min, int? max)
    {
        Min = min;
        Max = max;
    }

    public int? Min { get; }
    public int? Max { get; }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
