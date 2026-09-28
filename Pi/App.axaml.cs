using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FlexCompanion.ViewModels;
using FlexCompanion.Views;

namespace FlexCompanion;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new PiMainViewModel();
            var window = new MainWindow(vm);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => vm.Shutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
