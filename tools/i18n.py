"""Keeps the language files in sync with the source.

    python tools/i18n.py [dump.json]

1. Collects UI strings from the source (XAML {l:T ...} and string literals in the app project).
2. Adds run-time strings from a dump made with `Silica.exe --dump-strings dump.json`.
3. Rewrites src/Silica/Languages/en.json with every key (English maps to itself).
4. Prints, per language, the keys that still need a translation and the ones no longer used.
"""

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
APP = ROOT / "src" / "Silica"
CORE = ROOT / "src" / "Silica.Core"
LANGS = APP / "Languages"

XAML_KEY = re.compile(r"\{l:T\s+(?:'([^']+)'|([^,}']+))")
LITERAL = re.compile(r'(?<![\w@$])"((?:[^"\\\n]|\\.)*)"')

# Literals that look like words but are never shown to people.
TECHNICAL = re.compile(
    r"^(Segoe|Cascadia|Consolas|Silica\.|SilicaTray|Schedule\.Service|Brush\.|Text\.|Font\.|C\.|Button\.|Icon$|Card$|Panel$|Chip$|"
    r"Segment|Switch$|Divider$|NavItem|NavSection|SearchBox|PropLabel|PropValue|CellText|BareList|FocusVisual|"
    r"HKEY_|SOFTWARE\\|SELECT |pack://|[A-Z]:\\|\\\\|https?://|Global\\|ROOT\\|root\\|Win32_|MSFT_)"
)


# Found by the scanner but not shown to people: registry/WMI names, log messages, search keywords, doc examples.
IGNORE = {
    " GB", " threads", "), Filter = ", "AppsUseLightTheme", "Badge", "ChartsHost", "Collect ", "Compact", "Cond",
    "ConfiguredClockSpeed", "Core #10", "Core #2", "Core #3", "Core 10", "Core 2", "CurrentBuild", "DisplayVersion",
    "Empty language file", "ERROR", "WARN", "GPU2", "Headless report failed", "Instance activation", "Memory{T}",
    "Missing \"language\" section", "Missing language code", "NumberOfCores", "ProcessorNameString", "ProductName",
    "References", "ReportFormat", "Screenshot failed", "Sensor log not started", "Sensor log write failed",
    "Sensor logs", "Silica", "Silica bench ", "Silica overlay", "Silica-bench.tmp", "SMBIOSMemoryType", "Stable text",
    "Stable text: details", "Startup task", "TaskbarCreated", "UI error", "Unhandled", "Unobserved task", "UTF-16",
    "Windows 10", "Windows 11", "yyyy-MM-dd HH:mm:ss", "Report failed", "Report not saved", "Disk benchmark failed",
    "Stability test failed to start", "Languages", "Native name of the language", "English name of the language",
    "Task Scheduler is not available", "AES-NI", "no AES-NI", "AVX", "AVX-512", "AVX2+FMA", "FP32", "FP64", "FPU", "SSE",
    "GiB", "KiB", "MiB", "TiB", "SSD", "NET", "RAM",
    "CPU", "GPU", "DDR3", "DDR4", "DDR5", "LPDDR4", "LPDDR5", "Windows", "1 GiB buffer", "1 GiB buffer, non-temporal stores",
    "512 MiB random cyclic chain, 64-byte lines, 1 thread", "1 thread", "Stress test is already running.",
    "No stress components selected.", "1.5 GB", "512 MB", "Silica · ", "{exe}", "inverted address", "walking ones",
    "walking zeros",
}

# Used only through computed keys, so the scanner cannot see them.
EXTRA = {"now", "Stress test started", "{0} s"}

# Unit symbols go through Fmt.Unit, so they are keys too.
UNITS = {"B", "KB", "MB", "GB", "TB", "PB", "MHz", "GHz", "bps", "Kbps", "Mbps", "Gbps", "Tbps", "/s", "d",
         "V", "A", "W", "RPM", "L/h", "mWh", "dBA", "s", "Hz", "µS/cm"}

# Plural forms for keys used with Loc.N.
EN_VALUES = {
    "{0} sensors": "{0} sensor|{0} sensors",
    "{0} results": "{0} result|{0} results",
    "{0} threads": "{0} thread|{0} threads",
    "{0} times": "{0} time|{0} times",
}

EN_SYNONYMS = {
    "Overview": ["dashboard", "home", "summary"],
    "Sensors": ["temperature", "temps", "fans", "fan speed", "voltage", "monitoring", "hwmonitor"],
    "Graphics": ["gpu", "video card", "videocard", "graphics card", "display adapter", "vram"],
    "Processor": ["cpu", "chip", "cores"],
    "Memory": ["ram", "dimm", "memory modules", "sticks"],
    "Storage": ["disk", "drive", "ssd", "hdd", "nvme", "hard drive", "smart"],
    "Motherboard": ["mainboard", "mobo", "board", "bios", "chipset"],
    "Monitors": ["display", "screen", "edid"],
    "Network": ["ethernet", "wifi", "wi-fi", "internet", "lan", "ip address"],
    "Audio": ["sound", "speakers", "headphones", "microphone"],
    "Input devices": ["keyboard", "mouse", "gamepad", "controller"],
    "Devices": ["device manager", "pnp"],
    "Power": ["battery", "power plan", "charge"],
    "Operating system": ["windows", "os", "windows version"],
    "Processes": ["task manager", "running apps"],
    "Services": ["daemons"],
    "Startup": ["autorun", "autostart", "boot"],
    "Installed programs": ["apps", "software", "uninstall"],
    "Drivers": ["driver"],
    "Windows updates": ["patches", "hotfix", "kb"],
    "Security": ["antivirus", "defender", "firewall", "tpm", "bitlocker"],
    "Benchmarks": ["performance", "speed test", "score"],
    "Disk benchmark": ["ssd speed", "drive speed", "crystaldiskmark"],
    "Stability test": ["stress test", "burn in", "torture test"],
    "Monitor test": ["dead pixels", "screen test", "backlight bleed"],
    "Report": ["export", "save", "print"],
    "Settings": ["options", "preferences", "config"],
    "Theme": ["dark mode", "light mode"],
    "Tray icon": ["taskbar", "notification area"],
    "Overlay": ["osd", "on screen display", "always on top"],
    "Sensor logging": ["csv", "log"],
    "Alerts": ["notification", "overheat", "warning"],
    "Start with Windows": ["autostart", "boot"],
}

SEARCH_KEYWORDS = re.compile(r'\("[^"]+", "[a-z]+", "([a-z ]+)"\)')


ESCAPES = {"n": "\n", "r": "\r", "t": "\t", '"': '"', "\\": "\\"}


def unescape(s: str) -> str:
    return re.sub(r'\\(["\\nrt])', lambda m: ESCAPES[m.group(1)], s)


def looks_like_text(s: str) -> bool:
    if not s or len(s) > 300 or TECHNICAL.match(s):
        return False
    if "\\u" in s or any(0xE000 <= ord(c) <= 0xF8FF for c in s):
        return False
    if not re.search(r"[A-Za-z]{2}", s):
        return False
    if re.fullmatch(r"[a-z0-9\-_.:/]+", s):  # ids, file names, extensions
        return False
    if re.fullmatch(r"[A-Za-z]+\.[A-Za-z.]+", s):  # Type.Member, resource keys
        return False
    if s.startswith(("--", "*.")) or s.endswith((".dll", ".json", ".exe", ".xaml", ".ico", ".csv")):
        return False
    if s.startswith("{"):
        return bool(re.search(r"[A-Za-z]{2}", re.sub(r"\{\d+\}", "", s)))
    return s[0].isupper() or " " in s


def static_keys() -> set[str]:
    keys: set[str] = set(EXTRA) | UNITS
    for f in APP.rglob("*.xaml"):
        if "obj" in f.parts or "bin" in f.parts:
            continue
        for m in XAML_KEY.finditer(f.read_text(encoding="utf-8")):
            key = (m.group(1) or m.group(2)).strip()
            if key:
                keys.add(key)
    sources = [p for p in APP.rglob("*.cs") if "obj" not in p.parts and "bin" not in p.parts]
    sources += list((CORE / "Stress").glob("*.cs")) + list((CORE / "Bench").glob("*.cs"))
    for f in sources:
        text = f.read_text(encoding="utf-8")
        # Skip interop declarations and attributes.
        text = re.sub(r"\[(DllImport|LibraryImport|GeneratedRegex|JsonSerializable)[^\]]*\]", "", text)
        text = re.sub(r"^\s*///.*$", "", text, flags=re.M)
        keywords = {m.group(1) for m in SEARCH_KEYWORDS.finditer(text)}
        for m in LITERAL.finditer(text):
            s = unescape(m.group(1))
            if s.endswith(": "):
                s = s[:-2]
            if looks_like_text(s) and s not in IGNORE and s not in keywords:
                keys.add(s)
    return keys


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def save(path: Path, data: dict) -> None:
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    keys = static_keys()
    if len(sys.argv) > 1:
        keys |= {k for k in load(Path(sys.argv[1])) if k.strip()}

    en_path = LANGS / "en.json"
    en = load(en_path)
    en["strings"] = {k: EN_VALUES.get(k, k) for k in sorted(keys, key=str.casefold)}
    en["synonyms"] = EN_SYNONYMS
    save(en_path, en)
    print(f"en.json: {len(keys)} keys")

    for path in sorted(LANGS.glob("*.json")):
        if path.name == "en.json":
            continue
        pack = load(path)
        strings = pack.get("strings", {})
        missing = [k for k in en["strings"] if k not in strings]
        unused = [k for k in strings if k not in en["strings"]]
        print(f"{path.name}: {len(strings)} translated, {len(missing)} missing, {len(unused)} unused")
        if "--verbose" in sys.argv:
            for k in missing:
                print("  missing:", k)
            for k in unused:
                print("  unused: ", k)


if __name__ == "__main__":
    main()
