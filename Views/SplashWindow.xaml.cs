using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FlexCompanion.Views;

public partial class SplashWindow : Window
{
    private static readonly Uri SplashImageUri = new(
        "pack://application:,,,/FlexCompanion;component/Assets/FlexCompanionIcon.png",
        UriKind.Absolute);

    public SplashWindow()
    {
        InitializeComponent();
        LoadSplashImage();
    }

    private void LoadSplashImage()
    {
        try
        {
            // Load the embedded WPF resource eagerly. OnLoad releases the resource
            // stream immediately and avoids any dependency on the process working
            // directory or on a loose image file beside the executable.
            var resource = Application.GetResourceStream(SplashImageUri)
                ?? throw new FileNotFoundException(
                    "The embedded splash image resource could not be found.",
                    SplashImageUri.ToString());

            using (resource.Stream)
            {
                var decoder = BitmapDecoder.Create(
                    resource.Stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                if (decoder.Frames.Count == 0)
                    throw new InvalidDataException("The embedded splash image contains no bitmap frames.");

                var image = decoder.Frames[0];
                image.Freeze();
                SplashImage.Source = image;
            }
        }
        catch (Exception ex)
        {
            // A damaged branding asset must not prevent the application from
            // starting. Keep the branded fallback visible and leave a diagnostic
            // for debuggers/log listeners instead of surfacing a fatal UI dialog.
            Trace.TraceError($"Unable to load splash image '{SplashImageUri}': {ex}");
            SplashImage.Visibility = Visibility.Collapsed;
            SplashImageFallback.Visibility = Visibility.Visible;
        }
    }
}
