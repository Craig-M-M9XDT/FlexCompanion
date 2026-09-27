using System;
using System.IO;
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
