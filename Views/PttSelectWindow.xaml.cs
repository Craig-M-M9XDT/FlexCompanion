using System.Windows;
using FlexCompanion.ViewModels;

namespace FlexCompanion.Views;

public partial class PttSelectWindow : Window
{
    public PttSelectWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();
}