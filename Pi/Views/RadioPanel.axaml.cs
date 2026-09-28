using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlexCompanion.Views;

public sealed partial class RadioPanel : UserControl
{
    public RadioPanel()
    {
        InitializeComponent();
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
