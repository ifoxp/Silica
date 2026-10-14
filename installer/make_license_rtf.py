"""Writes installer/License.rtf from LICENSE (the license page of the MSI needs RTF)."""

from pathlib import Path

here = Path(__file__).resolve().parent
text = (here.parent / "LICENSE").read_text(encoding="utf-8").strip()
paragraphs = [p.replace("\n", " ") for p in text.split("\n\n")]


def escape(s: str) -> str:
    return s.replace("\\", "\\\\").replace("{", "\\{").replace("}", "\\}")


body = "\\par\\par ".join(escape(p) for p in paragraphs)
rtf = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Segoe UI;}}\\f0\\fs18 " + body + "\\par}"
(here / "License.rtf").write_text(rtf, encoding="ascii")
