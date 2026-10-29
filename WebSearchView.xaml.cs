using System.Windows;
using System.Windows.Controls;

namespace MangaLibraryApp;

public partial class WebSearchView : UserControl
{
    public WebSearchView()
    {
        InitializeComponent();
    }

    private void ActiveSource_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: WebSourceOption option })
            (DataContext as WebSearchViewModel)?.SelectActive(option);
    }

    private void Roadmap_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: WebSourceOption option })
            (DataContext as WebSearchViewModel)?.SelectRoadmap(option);
    }
}
