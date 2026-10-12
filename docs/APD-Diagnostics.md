# SmartSignal APD diagnostics (Windows preview)

The **APD** tab in each Companion radio slot opens a separate full-loop diagnostics
window. This is an initial Windows/WPF implementation, not a replacement for
SmartSDR's APD controls or for calibrated RF instrumentation.

## What is live

- Radio APD enabled/configurable/equalizer-active fields, from `sub apd all`
  status. **Active** means the firmware reports its equalizer active; it does
  not prove a specific dB of IMD improvement.
- Selected sampler for each TX antenna and valid sampler choices **reported by
  the radio**. A sampler map without the `configurable` flag remains read-only.
- Radio power, SWR, microphone peak and compressor peak from Companion's existing
  meter subscription and TX status paths.
- Power Genius XL output watts and SWR, **only** when the existing PGXL TCP
  telemetry is connected and its Station target slot matches the APD window's
  radio. PGXL telemetry is not automatically associated with other radios.

The enable/disable, equalizer reset and sampler apply buttons send documented
protocol commands and report command success/error. The window does not trigger
PTT, tune, two-tone transmit or auto-calibration. Hardware controls remain with
the radio. The user must independently verify RF wiring before using external
samplers.

## What's calculated, not measured

Coupler loss and additional attenuation fields are operator-entered physical RF
losses (dB), defaulting to **60 dB** and **0 dB**. They **do not control an
attenuator**. If real PGXL output-power telemetry is available and the radio is
currently configured for an external sampler:

```text
estimated RF sample dBm = 10 * log10(PGXL output watts * 1000)
                          - coupler coupling loss dB
                          - extra pad / cable attenuation dB
```

This is a power-budget estimate. It does not detect an actual sample at the
FLEX connector, tell whether an ADC is overloaded, prove sampler isolation, or
assure safe RF input power. For example, 1500 W through a perfect -60 dB tap
with no other losses *calculates* to about +1.76 dBm. This should **not** be
interpreted as a vendor-rated input limit.

Feedback ADC level, correction coefficients, ALC, calibrated IMD spectra and
before/after distortion are **not reported** by the currently verified public
API/telemetry in this integration; the display deliberately leaves these
unmeasured.

## Initial on-radio validation checklist

1. On an APD-capable FLEX-8000/Aurora radio, connect Companion and confirm APD
   fields arrive. On a FLEX-6000, ensure all APD commands stay disabled.
2. Select the APD tab and open diagnostics. Compare the enable and active status
   against SmartSDR/AetherSDR. Toggle APD and verify radio acknowledgement.
3. Check the sampler map for each actual TX antenna. Apply **only** a port
   presented by firmware. Confirm the selected port subsequently updates via
   status. Verify that sampler apply and equalizer reset are blocked on TX.
4. If using PGXL, wire the manufacturer's **sample output**, not its RF power
   output, to the correct designated FLEX sampler input. Verify manufacturer RF
   limits, port routing, isolation, and 50-ohm cable requirements first.
5. Connect PGXL telemetry from Companion's Station panel, and assign its target
   to the matching radio slot. Compare radio output watts and PGXL output watts
   with other instruments; the values represent **different points** in the
   transmit chain.
6. Enter the real coupler / pad loss. Confirm the feedback readout explicitly
   says **ESTIMATED** and changes without issuing any RF hardware command.
7. Disconnect PGXL, disconnect the radio, switch slot target, and reconnect:
   prior values must not masquerade as live telemetry.

## Wire protocol exercised

```text
sub apd all
apd enable=1
apd enable=0
apd reset
apd sampler tx_ant=ANT1 sample_port=RX_A
```

Actual available `tx_ant` and `sample_port` options come from the individual
radio. The example above is **not** a universal mapping. Companion only sends a
sampler change that matches the actual `valid_samplers` status.

References:
- [FlexRadio: Configuring SmartSignal APD and PGXL](https://helpdesk.flexradio.com/hc/en-us/articles/49290951647899-Configuring-SmartSignal-APD-for-FlexRadio-8000-and-the-Power-Genius-XL-Amplifier)
- [AetherSDR command mapping](https://github.com/aethersdr/AetherSDR/blob/main/src/core/backends/flex/FlexModelControls.cpp)
- [AetherSDR APD status decoding](https://github.com/aethersdr/AetherSDR/blob/main/src/core/backends/flex/FlexBackend.cpp)

## Planned later phases

Add calibrated sampler ADC feedback measurements and distortion graphs **only**
after verifying a public readout or an independently measured RF/IQ source.
Programmable RF attenuator support requires a specified, compatible external
device, validated RF-safe limits and explicit operator actions. Linux/Pi UI
parity is a separate later step.
