using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlexCompanion.Views;

namespace FlexCompanion;

public partial class App : Application
{
    int _handlingFatalUiException;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF uses Direct3D hardware acceleration by default. Do not force
        // SoftwareOnly rendering here; leaving the default allows WPF to use the GPU.
        // FFT compute remains on background CPU workers because these small transforms
        // are faster there than paying a GPU upload/download penalty every 33-50 ms.

        // Mouse wheel nudges any slider by SmallChange (hold Ctrl for LargeChange).
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnSliderWheel));

        DispatcherUnhandledException += (_, args) =>
        {
            // A render exception can be raised again immediately if WPF is allowed to
            // continue painting the same broken visual. Never open an endless stack of
            // warning dialogs: report the first failure once, then close cleanly.
            args.Handled = true;
            if (Interlocked.Exchange(ref _handlingFatalUiException, 1) != 0) return;
            var ex = args.Exception;
            var detail = ex.InnerException is null
                ? ex.Message
                : $"{ex.Message}\n\nInner exception: {ex.InnerException.Message}";
            MessageBox.Show($"{detail}\n\nFlex Companion must close.", "Flex Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(-1);
        };

        var splash = new SplashWindow();
        splash.Show();

        // Give the splash enough time to be visible while the main view model starts.
        await Task.Delay(1200);

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
        splash.Close();
    }

    static void OnSliderWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider s || !s.IsEnabled) return;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? s.LargeChange : s.SmallChange;
        if (step <= 0) step = (s.Maximum - s.Minimum) / 100.0;
        s.Value = Math.Clamp(s.Value + Math.Sign(e.Delta) * step, s.Minimum, s.Maximum);
        e.Handled = true;
    }
}
