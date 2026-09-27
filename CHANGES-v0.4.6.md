# Flex Companion v0.4.6

- Fixed the About window startup exception caused by a relative image/icon URI being resolved from the `Views` folder. The About window now uses an application pack URI for the embedded icon.
- Added a branded startup splash screen using the CC Flex Companion icon.
- Splash screen identifies Craig Magee M9XDT as creator and notes GPLv3 / AetherSDR acknowledgement.
- Application startup now explicitly shows the splash, creates the main window, then closes the splash.
- Improved unhandled-exception dialog to include the inner exception message where available, making future XAML/runtime errors easier to diagnose.
