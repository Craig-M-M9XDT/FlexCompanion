"""Radio-authoritative automatic PC microphone preservation policy.

Never generate a MOX/key command. Only request PTT Override OFF if a radio
explicitly advertises ptt_override=1 in its normal status and is in RX/READY.
Older 4.2.x firmware does not advertise a writable control.
"""
from dataclasses import dataclass


@dataclass
class PttOverrideState:
    auto: bool = True
    mic: str = ""
    source: str = ""
    interlock_state: str = ""
    override: bool | None = None
    plane: str = ""
    attempted: bool = False
    inflight: bool = False
    failed: bool = False
    accepted: bool = False

    def reset(self) -> None:
        keep_auto = self.auto
        self.__dict__.update(type(self)(auto=keep_auto).__dict__)

    def observe(self, plane: str, fields: dict[str, str]) -> str | None:
        if plane not in ("interlock", "transmit"):
            return None
        if plane == "transmit" and "mic_selection" in fields:
            self.mic = fields["mic_selection"].strip().upper()
        if plane == "interlock":
            if "source" in fields:
                self.source = fields["source"].strip().upper()
            if "state" in fields:
                self.interlock_state = fields["state"].strip().upper()
        reported = fields.get("ptt_override")
        if reported in ("0", "1"):
            on = reported == "1"
            if self.override != on or self.plane != plane:
                self.override = on
                self.plane = plane
                self.attempted = self.inflight = self.failed = self.accepted = False
        return self.maybe_command()

    def maybe_command(self) -> str | None:
        if (not self.auto or self.mic != "PC" or self.override is not True
                or self.interlock_state not in ("READY", "RECEIVE")
                or self.attempted or self.inflight):
            return None
        cmd = {"transmit": "transmit set ptt_override=0",
               "interlock": "interlock ptt_override=0"}.get(self.plane)
        if cmd:
            self.attempted = True
            self.inflight = True
        return cmd

    def complete(self, code: int) -> None:
        self.inflight = False
        self.failed = code != 0
        self.accepted = code == 0

    @property
    def source_text(self) -> str:
        return f"Selected mic: {self.mic or 'waiting'}  •  PTT: {self.source or 'unobserved'} ({self.interlock_state or 'unknown'})"

    @property
    def summary(self) -> str:
        if not self.auto:
            return "Auto PC-mic preservation OFF. Radio unchanged."
        if self.override is None:
            return "Radio does not report a PTT Override API. Companion cannot change pre-4.3 firmware routing."
        if self.mic != "PC":
            return "Monitoring: PC microphone not selected."
        if self.override is False:
            return "Radio confirms hardware PTT Override OFF — PC audio preserved."
        if self.failed:
            return "PTT Override API write rejected; confirm firmware command route."
        if self.accepted:
            return "Disable command accepted; awaiting radio status confirmation."
        if self.inflight:
            return "Disabling radio hardware-mic override…"
        return "PTT Override ON; waiting for interlock idle before updating."
