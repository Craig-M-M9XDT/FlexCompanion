# Flex Companion v0.5.1.1

- Fixed the v0.5.1 build error in the source-paced FLEX meter callback by explicitly using the existing `FlexCompanion.Flex.Ui.Post` WPF dispatcher helper.
- Preserves the v0.5.1 source-paced meter and Aether shared-pan FFT logic without changing its cadence, coalescing, fallback or watchdog behavior.
