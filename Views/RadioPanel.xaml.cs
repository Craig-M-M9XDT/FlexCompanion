using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlexCompanion.ViewModels;

namespace FlexCompanion.Views;

public partial class RadioPanel : UserControl
{
    bool _txTabAdded;
    ApdWindow? _apdWindow;

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

        var apdPanel = new StackPanel { Margin = new Thickness(12) };
        apdPanel.Children.Add(new TextBlock
        {
            Text = "SmartSignal APD: see radio and PGXL TX meters, live APD state and sampler configuration, with a clearly labelled feedback power estimate.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });
        var open = new Button { Content = "Open APD diagnostics", Padding = new Thickness(12, 6, 12, 6) };
        open.Click += OpenApdWindow;
        apdPanel.Children.Add(open);
        tabs.Items.Add(new TabItem { Header = "APD", Content = apdPanel });
        _txTabAdded = true;
    }

    void OpenApdWindow(object sender, RoutedEventArgs e)
    {
        if (_apdWindow?.IsVisible == true) { _apdWindow.Activate(); return; }
        var owner = Window.GetWindow(this);
        if (DataContext is not RadioViewModel radio || owner?.DataContext is not MainViewModel main)
            return;
        var window = new ApdWindow(radio, main.Station) { Owner = owner };
        _apdWindow = window;
        window.Closed += (_, _) => _apdWindow = null;
        window.Show();
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
