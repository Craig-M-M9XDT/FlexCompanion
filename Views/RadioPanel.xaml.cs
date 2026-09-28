using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FlexCompanion.Views;

public partial class RadioPanel : UserControl
{
    bool _txTabAdded;

    public RadioPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_txTabAdded) return;
        var tabs = FindDescendant<TabControl>(this);
        if (tabs == null) return;

        tabs.Items.Add(new TabItem
        {
            Header = "TX",
            Content = new TxControls()
        });
        _txTabAdded = true;
    }

    static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }
}
