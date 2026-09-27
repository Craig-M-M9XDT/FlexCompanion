# Flex Companion v0.4.8

- Removed the native DVK tab and DVK command path from the companion UI.
- Reason: FLEX radios can reject native DVK commands when issued by a bound non-GUI companion client (for example 0x50004001), even though the same commands work from a full GUI client such as SmartSDR or AetherSDR.
- Flex Companion remains a non-GUI client so it can safely run alongside SmartSDR/AetherSDR without consuming or competing for a GUI-client station slot.
- Removed DVK subscriptions, slot model/state, record/play/preview/stop commands, and stale DVK-specific error text.
- Retained station binding for features that work correctly from a non-GUI client, such as CW auto tune.
- Future direction: implement independent local Voice Macros rather than presenting radio-native DVK controls that the radio may refuse.
