using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FlexCompanion.ViewModels;

namespace FlexCompanion.Views;

public sealed partial class MainWindow : Window
{
    readonly PiMainViewModel _vm;

    public MainWindow(PiMainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        Width = Math.Max(MinWidth, vm.Settings.WindowWidth);
        Height = Math.Max(MinHeight, vm.Settings.WindowHeight);

        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Opened += (_, _) => OnOpened();
        Closing += (_, _) => SaveWindowState();
        _vm.PropertyChanged += OnVmPropertyChanged;
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel);

        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => a.Equals("--touch", StringComparison.OrdinalIgnoreCase))
            || Environment.GetEnvironmentVariable("FLEXCOMPANION_TOUCH") == "1")
            _vm.TouchMode = true;
        if (args.Any(a => a.Equals("--kiosk", StringComparison.OrdinalIgnoreCase)))
            _vm.KioskMode = true;

        ApplyModeClasses();
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    void OnOpened()
    {
        // Small Pi panels work best maximized. Larger desktop displays preserve the saved size.
        var screen = Screens.Primary;
        if (screen != null)
        {
            double logicalW = screen.WorkingArea.Width / Math.Max(0.1, screen.Scaling);
            double logicalH = screen.WorkingArea.Height / Math.Max(0.1, screen.Scaling);
            if (logicalW <= 1100 || logicalH <= 650)
                WindowState = WindowState.Maximized;
        }
        if (_vm.KioskMode) WindowState = WindowState.FullScreen;
        ApplyResponsiveLayout();
    }

    void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch && !_vm.TouchMode)
            _vm.TouchMode = true;
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PiMainViewModel.TouchMode)
            || e.PropertyName == nameof(PiMainViewModel.MicroMode))
            ApplyModeClasses();
        else if (e.PropertyName == nameof(PiMainViewModel.KioskMode))
        {
            WindowState = _vm.KioskMode ? WindowState.FullScreen : WindowState.Maximized;
            ApplyModeClasses();
        }
    }

    void ApplyResponsiveLayout()
    {
        double w = ClientSize.Width;
        double h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;

        // Below this width two full control surfaces are less usable than one large touch tab.
        _vm.CompactMode = w < 1080 || h < 650;
        _vm.MicroMode = w < 850 || h < 560;
        ApplyModeClasses();
    }

    void ApplyModeClasses()
    {
        SetClass("touch", _vm.TouchMode);
        SetClass("micro", _vm.MicroMode);
        SetClass("compact", _vm.CompactMode);
    }

    void SetClass(string name, bool on)
    {
        if (on)
        {
            if (!Classes.Contains(name)) Classes.Add(name);
        }
        else
        {
            if (Classes.Contains(name)) Classes.Remove(name);
        }
    }

    void SaveWindowState()
    {
        if (WindowState == WindowState.Normal)
        {
            _vm.Settings.WindowWidth = Math.Max(MinWidth, ClientSize.Width);
            _vm.Settings.WindowHeight = Math.Max(MinHeight, ClientSize.Height);
        }
        _vm.Settings.Save();
    }
}
