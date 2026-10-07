"""Loads the native C++ core, falling back to the numpy implementation.

Set ``FLEXCOMPANION_PURE_PYTHON=1`` to force the fallback (useful for comparing the two).
"""
from __future__ import annotations

import os

core = None
if os.environ.get("FLEXCOMPANION_PURE_PYTHON") != "1":
    try:
        from . import flexcore as core  # type: ignore[attr-defined]
    except ImportError:
        core = None

if core is None:
    from . import _fallback as core  # noqa: F811

NATIVE: bool = getattr(core, "NATIVE", True)
BACKEND_NAME = "C++ core" if NATIVE else "numpy fallback"

peek = core.peek
is_iq_pcc = core.is_iq_pcc
parse_meters = core.parse_meters
parse_audio = core.parse_audio
fft_db = core.fft_db
parse_fcsp = core.parse_fcsp
resample_linear = core.resample_linear
SpectrumEngine = core.SpectrumEngine
PCC_METER = core.PCC_METER
