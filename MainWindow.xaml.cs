using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FlexCompanion.ViewModels;
using FlexCompanion.Views;

namespace FlexCompanion;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        Width = Math.Max(MinWidth, _vm.Settings.WindowWidth);
        Height = Math.Max(MinHeight, _vm.Settings.WindowHeight);

        _vm.PropertyChanged += OnVmChanged;
        ApplyDualMode();
        StateChanged += (_, _) => ApplyWindowState();
        Closing += OnClosing;
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.DualMode)) ApplyDualMode();
    }

    // Slot B collapses completely (column width 0) when dual mode is off.
    void ApplyDualMode()
    {
        bool dual = _vm.DualMode;
        PanelB.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        ColB.Width = dual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    void ApplyWindowState()
    {
        // A borderless maximised window overhangs the screen by the resize border; pad it back in.
        RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnClose(object sender, RoutedEventArgs e) => Close();

    void OnAbout(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        var size = WindowState == WindowState.Normal ? new Size(ActualWidth, ActualHeight) : RestoreBounds.Size;
        if (size.Width > 0 && size.Height > 0 && !double.IsInfinity(size.Width))
        {
            _vm.Settings.WindowWidth = size.Width;
            _vm.Settings.WindowHeight = size.Height;
        }
        _vm.Shutdown();
    }
}
