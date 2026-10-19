using System.Windows;
using FlexCompanion.ViewModels;

namespace FlexCompanion.Views;

public partial class LicenseStatusWindow : Window
{
    public LicenseStatusWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
