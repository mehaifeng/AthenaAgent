"""Small cross-platform runtime fixes shared by the optional MiniMax helpers."""

import sys


def _configure_utf8(stream) -> None:
    reconfigure = getattr(stream, "reconfigure", None)
    if reconfigure is not None:
        try:
            reconfigure(encoding="utf-8", errors="replace")
        except (OSError, ValueError):
            pass


_configure_utf8(sys.stdout)
_configure_utf8(sys.stderr)
