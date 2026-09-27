# Flex Companion v0.4.3

## Debug fixes on top of v0.4.2
- DAX IQ ownership: when another program clears the pan DAX-IQ channel, Companion now really stays hands-off until FFT is toggled off/on. v0.4.2 accidentally cleared the remembered pan id during teardown, allowing a later pan status to re-acquire the channel.
- Disconnect cleanup: added a best-effort no-reply FLEX command write so Companion-created pan DAX-IQ assignments are released before the TCP socket is disposed. The prior fire-and-forget `SendAsync` could lose the cleanup command.
- AGC-T disconnect: an in-progress or unconfirmed calibration now best-effort restores the original AGC value before an orderly disconnect closes the radio connection.
- DX Cluster / PGXL lifecycle: explicit disconnect now retires stale read/poll/reconnect continuations by generation, and connection startup clears stale `IsConnected` state.
- PGXL: stale read loops no longer post an error after they have been superseded by a newer connection.

## Validation performed here
- All XAML files parse as XML.
- C# brace/structure sanity scan passes.
- v0.4.1 missing `System.IO` fixes remain present.
- DAX-IQ command spelling (`stream create type=dax_iq` and `stream set ... daxiq_rate=`) was cross-checked against current AetherSDR source.

A Windows .NET 8/WPF build and live-radio test are still required for final validation.
