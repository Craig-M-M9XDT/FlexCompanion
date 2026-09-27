# Flex Companion v0.4.10.1

- Fixed Windows build error in `App.xaml.cs` caused by referencing `RenderOptions` from the wrong namespace.
- Removed the unnecessary explicit `ProcessRenderMode = Default` assignment. WPF already uses hardware-accelerated Direct3D rendering by default unless software rendering is forced elsewhere.
- No FFT/DAX-IQ performance logic changed from v0.4.10.
