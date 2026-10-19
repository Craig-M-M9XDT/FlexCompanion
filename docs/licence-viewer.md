# Read-only SmartSDR licence status viewer

Open **Licences** in the Windows title bar. Radio A/B tabs show radio-reported
feature names, states, reasons, subscriptions/expiry and licence summary
fields if received.

## Implementation

The Companion's existing `sub license all` subscription is used.
No additional login, radio connection or undocumented diagnostic command is used.
The viewer receives normal `S<handle>|license ...` messages over the pre-existing
SmartSDR TCP API session.

The fields seen in static FLEX-6500 and FLEX-6600M firmware include:

- `license feature name=... enabled=0|1 reason=...`
- `license subscription name=... expiration=...`

The exact full TCP framing and field coverage still require live validation.
A radio may return a nonzero subscription reply or may not report every field,
particularly on older firmware. Missing flags are not treated as disabled.
Values shown are **observations**, not authorisation decisions.

The feature list previously recovered from two ARM builds includes
`ALPHA, SMARTLINK, MULTIFLEX, WFP, NOISE_FLOOR, NOISE_REDUCTION,
DIGITAL_VOICE_KEYER, AUTO_TUNE, DIV_ESC`, with newer 6500 firmware also
including `NAVTEX_WF, RAPIDM_LOGGING, FILTER_PRESET_CONF, PANADAPTER_VISUALS`.
Not all identifiers exist on all models/firmware revisions.

No licence files, signing keys, radio entitlements, firmware or factory
state are created, modified or transmitted by this viewer.

## Verification

`dotnet run --project LicenseTests/LicenseTests.csproj -c Release`

Windows build:

`dotnet build FlexCompanion.csproj -c Release`

The live status feed has not been tested against a connected physical radio.
Do not assume a blank feature list means a licence is missing.
