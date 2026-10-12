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
}
