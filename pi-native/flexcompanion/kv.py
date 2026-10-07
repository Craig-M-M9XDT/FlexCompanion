"""Helpers for the SmartSDR ``key=value key=value`` text protocol (port of Kv.cs)."""
from __future__ import annotations

from typing import Dict, Iterable, List, Optional


class CIDict(dict):
    """Case-insensitive string-keyed dict (the FLEX protocol is case-insensitive on keys)."""

    def __init__(self, *args, **kwargs):
        super().__init__()
        self.update(*args, **kwargs)

    def __setitem__(self, key, value):
        super().__setitem__(key.lower(), value)

    def __getitem__(self, key):
        return super().__getitem__(key.lower())

    def __contains__(self, key):
        return isinstance(key, str) and super().__contains__(key.lower())

    def __delitem__(self, key):
        super().__delitem__(key.lower())

    def get(self, key, default=None):
        return super().get(key.lower(), default)

    def pop(self, key, *default):
        return super().pop(key.lower(), *default)

    def update(self, *args, **kwargs):
        for k, v in dict(*args, **kwargs).items():
            self[k] = v

    def copy(self) -> "CIDict":
        return CIDict(self)


def tokenize(s: str) -> List[str]:
    """Split on spaces, keeping "quoted strings" together (quotes removed)."""
    out: List[str] = []
    buf: List[str] = []
    quoted = False
    for c in s:
        if c == '"':
            quoted = not quoted
            continue
        if c == " " and not quoted:
            if buf:
                out.append("".join(buf))
                buf.clear()
            continue
        buf.append(c)
    if buf:
        out.append("".join(buf))
    return out


def parse(tokens: Iterable[str]) -> CIDict:
    """Tokens -> case-insensitive dict. 0x7F is the radio's encoding for a space."""
    d = CIDict()
    for t in tokens:
        i = t.find("=")
        if i > 0:
            d[t[:i]] = t[i + 1:].replace("\x7f", " ").replace("\xa0", " ")
    return d


def parse_line(s: str) -> CIDict:
    return parse(tokenize(s))


def to_float(s: Optional[str]) -> Optional[float]:
    if s is None:
        return None
    try:
        return float(s)
    except (TypeError, ValueError):
        return None


def is_on(value: Optional[str]) -> bool:
    return value is not None and value.lower() in ("1", "on", "true")


def same_handle(a: str, b: str) -> bool:
    """Compare FLEX client handles ignoring 0x prefix and leading zeros."""
    def n(h: str) -> str:
        h = h[2:] if h.lower().startswith("0x") else h
        return h.lstrip("0").upper()
    return n(a or "") == n(b or "")


def parse_hex_id(text: Optional[str]) -> int:
    """'0x40000000' / '40000000' / '0x4..|extra' -> int, or 0."""
    s = (text or "").strip()
    if not s:
        return 0
    s = s.replace("|", " ").split()[0]
    if s.lower().startswith("0x"):
        s = s[2:]
    try:
        return int(s, 16)
    except ValueError:
        return 0


def as_dict(d: Dict[str, str]) -> CIDict:
    return d if isinstance(d, CIDict) else CIDict(d)
