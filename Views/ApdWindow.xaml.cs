using System.Windows;
using FlexCompanion.ViewModels;

namespace FlexCompanion.Views;

public partial class ApdWindow : Window
{
    readonly ApdDashboardViewModel _vm;

    public ApdWindow(RadioViewModel radio, StationViewModel station)
    {
        InitializeComponent();
        _vm = new ApdDashboardViewModel(radio, station);
        DataContext = _vm;
        Closed += (_, _) => _vm.Dispose();
    }

    void SaveApdDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save APD development diagnostics (no transmission)",
            Filter = "Text log (*.txt)|*.txt",
            FileName = $"FlexCompanion-APD-Lab-{_vm.Radio.Slot}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            DefaultExt = ".txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, _vm.Radio.CreateApdDiagnosticSnapshot());
            MessageBox.Show(this, "APD status and meter snapshot saved. No radio settings were changed.",
                "APD development diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save APD diagnostic snapshot: " + ex.Message,
                "APD diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
