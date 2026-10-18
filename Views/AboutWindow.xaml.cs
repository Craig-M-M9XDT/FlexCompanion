using System;
using System.IO;\nusing System.Diagnostics;
using System.Windows;

namespace FlexCompanion.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        LoadTexts();
    }

    private void LoadTexts()
    {
        var baseDir = AppContext.BaseDirectory;
        LicenseTextBox.Text = ReadTextOrFallback(Path.Combine(baseDir, "LICENSE"),
            "GNU GPL v3 licence file was not found alongside the application package.");
        NoticeTextBox.Text = ReadTextOrFallback(Path.Combine(baseDir, "NOTICE.md"),
            "Acknowledgements file was not found alongside the application package.");
    }

    private static string ReadTextOrFallback(string path, string fallback)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : fallback;
        }
        catch (Exception ex)
        {
            return $"{fallback}\n\nRead error: {ex.Message}";
        }
    }

    private void OnDonate(object sender, RoutedEventArgs e)\n    {\n        try { Process.Start(new ProcessStartInfo("https://paypal.me/CraigM9XDT") { UseShellExecute = true }); }\n        catch (Exception ex) { MessageBox.Show(this, $"Unable to open PayPal: {ex.Message}", "Donation link", MessageBoxButton.OK, MessageBoxImage.Information); }\n    }\n\n    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
