using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlexCompanion.ViewModels;

namespace FlexCompanion.Views;

public partial class RadioPanel : UserControl
{
    bool _txTabAdded;
    ApdWindow? _apdWindow;
#if APD_LAB
    static System.Diagnostics.Process? s_apdSimulator;
#endif

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
#if APD_LAB
        apdPanel.Children.Add(new TextBlock
        {
            Text = "NO FLEX-8000 REQUIRED: APD LAB includes a local virtual FLEX-8600. Test data is SYNTHETIC; no RF can be transmitted.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 9),
            Foreground = Brushes.Orange
        });
        var simulate = new Button
        {
            Content = "Start simulated FLEX-8600 (NO RF)",
            Padding = new Thickness(12, 7, 12, 7),
            ToolTip = "Launches localhost-only simulated radio and connects this Companion slot. Does not contact physical hardware."
        };
        simulate.Click += StartApdSimulator;
        apdPanel.Children.Add(simulate);
#endif
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

#if APD_LAB
    async void StartApdSimulator(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RadioViewModel radio) return;
        if (radio.IsConnected || radio.IsConnecting)
        {
            MessageBox.Show("Disconnect this radio slot before starting the APD simulator. Other radio slots are unaffected.",
                "APD LAB", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "APD-Simulator.exe");
        if (!System.IO.File.Exists(exe))
        {
            MessageBox.Show("APD-Simulator.exe is missing. Extract the complete APD LAB portable ZIP first.",
                "APD LAB", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            if (s_apdSimulator == null || s_apdSimulator.HasExited)
            {
                s_apdSimulator = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true
                });
            }
            await Task.Delay(1000);
            if (s_apdSimulator == null || s_apdSimulator.HasExited)
            {
                MessageBox.Show("Simulator did not start. It uses 127.0.0.1:4992 only. Check that another app isn't using that port.",
                    "APD LAB", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await radio.ConnectAsync("127.0.0.1", 4992, "APD SIMULATOR — SYNTHETIC / NO RF");
            if (radio.IsConnected) OpenApdWindow(sender, e);
            else MessageBox.Show("The local simulator did not answer. See the simulator console for details.",
                "APD LAB", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start APD LAB simulator: " + ex.Message,
                "APD LAB", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
#endif

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
