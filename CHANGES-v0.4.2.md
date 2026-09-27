# Flex Companion v0.4.2

## New
- FFT width is user-selectable per radio slot: 3, 6, 12, 24, 48, 96 or 192 kHz (saved in settings).
  The DAX IQ rate follows the width (24/48/96/192 kHz) and the FFT size grows for narrow spans,
  so 3 kHz still shows ~500 real bins instead of a stretched 48 kHz view.
- FFT window is centred on the selected slice (not the pan centre) whenever the slice is inside the
  IQ bandwidth; frequency labels are slice-relative and an amber marker shows the slice.

## Fixes
- DX cluster: login never happened on DX Spider / AR-Cluster, because their "login:" prompt has no
  line ending and the old ReadLine loop waited forever. Now reads byte-by-byte, detects the prompt,
  and strips/refuses Telnet negotiation bytes.
- DX cluster: reconnecting while connected could leave the old read loop running; it then marked the
  new connection as disconnected and started a reconnect loop.
- PGXL: every reconnect started another status poll loop (poll traffic grew each time); poll writes
  and user commands could interleave on the socket; automatic reconnect only tried once.
- DAX IQ: if another program changed or cleared the pan's DAX IQ channel, the FFT silently stopped.
  It now follows a moved channel, and stops (without fighting) if the channel was cleared.
- DAX IQ: a pan DAX IQ channel Companion assigned was left set on the radio after Disconnect / exit.
- TUNE / MOX buttons flipped their state even when the radio refused the command.
- Band buttons and spot clicks tuned with autopan=0, so the panadapter didn't follow the jump.
- Station status now shows a readable error text instead of a blank on refused commands.
