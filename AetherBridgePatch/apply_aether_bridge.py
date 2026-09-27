#!/usr/bin/env python3
"""Apply the Flex Companion localhost pan-spectrum bridge to an AetherSDR checkout.

Usage:
    python apply_aether_bridge.py C:\\src\\AetherSDR
    python3 apply_aether_bridge.py ~/src/AetherSDR

The patch is deliberately tiny: it taps RadioModel::panFeedSpectrumReady, which is
already the source used by Aether's mini-pan, and mirrors those decoded FFT bins to
127.0.0.1:7331. It creates no additional radio pan, stream or DAX channel.
"""
from pathlib import Path
import sys

if len(sys.argv) != 2:
    raise SystemExit("Usage: apply_aether_bridge.py <AetherSDR source folder>")

root = Path(sys.argv[1]).expanduser().resolve()
path = root / "src" / "gui" / "MainWindow_Session.cpp"
if not path.exists():
    raise SystemExit(f"Not found: {path}")

text = path.read_text(encoding="utf-8")
marker = "// FLEX COMPANION LOCAL PAN BRIDGE (FCSP v1)"
if marker in text:
    print("Flex Companion bridge is already present.")
    raise SystemExit(0)

include_anchor = "#include <QTimer>"
if include_anchor not in text:
    raise SystemExit("Could not find the QTimer include anchor. Aether source layout has changed.")
text = text.replace(
    include_anchor,
    include_anchor + "\n#include <QDataStream>\n#include <QHostAddress>\n#include <QIODevice>\n#include <QUdpSocket>",
    1,
)

insert_anchor = "    // ── S History Markers — tap into FFT frames for voice signal detection ──"
if insert_anchor not in text:
    raise SystemExit("Could not find the pan-spectrum wiring anchor. Aether source layout has changed.")

bridge = r'''    // FLEX COMPANION LOCAL PAN BRIDGE (FCSP v1)
    // Mirror the radio-generated FFT bins Aether already receives to localhost only.
    // Flex Companion uses these as its compact spectrum source, avoiding a second
    // DAX-IQ stream and a second FFT. No radio object or network stream is created.
    connect(&m_radioModel, &RadioModel::panFeedSpectrumReady, this,
            [this](quint32 streamId, const QVector<float>& bins, qint64 emittedNs) {
        if (bins.size() < 2 || streamId == 0) return;

        QByteArray serial = m_radioModel.chassisSerial().trimmed().toUtf8();
        if (serial.isEmpty()) serial = m_radioModel.serial().trimmed().toUtf8();
        if (serial.isEmpty()) return;
        if (serial.size() > 1024) serial.truncate(1024);

        const int count = std::min<int>(bins.size(), 16384);
        QByteArray packet;
        packet.reserve(24 + serial.size() + count * int(sizeof(float)));
        QDataStream out(&packet, QIODevice::WriteOnly);
        out.setByteOrder(QDataStream::LittleEndian);
        out.setFloatingPointPrecision(QDataStream::SinglePrecision);
        out.writeRawData("FCSP", 4);
        out << quint8(1) << quint8(0);                       // version, flags
        out << quint16(serial.size());
        out << quint32(streamId);
        out << quint16(count) << quint16(0);                 // bin count, reserved
        out << qint64(emittedNs);
        out.writeRawData(serial.constData(), serial.size());
        for (int i = 0; i < count; ++i) out << bins[i];

        static QUdpSocket* bridgeSocket = new QUdpSocket(QCoreApplication::instance());
        bridgeSocket->writeDatagram(packet, QHostAddress::LocalHost, 7331);
    });

'''
text = text.replace(insert_anchor, bridge + insert_anchor, 1)
path.write_text(text, encoding="utf-8")
print(f"Patched: {path}")
print("Rebuild AetherSDR normally. The bridge sends localhost UDP/7331 only.")
