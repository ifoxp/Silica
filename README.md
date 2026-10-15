**English** · [Українська](README.uk.md)

<p align="center">
  <img src="docs/logo.png" width="96" alt="">
</p>

# Silica

System information, live sensors, benchmarks and a stability test for Windows —
free, open source, no accounts, no subscriptions, no telemetry.

Built in C# on .NET 9 and WPF. Sensor access goes through LibreHardwareMonitor
and the PawnIO driver; everything else — the information pages, benchmarks,
stability test, reports and search — is written from scratch.

![Overview](docs/screenshots/overview.png)

## Download

Take the latest build from [Releases](https://github.com/ifoxp/Silica/releases/latest):

| File | What it is |
|---|---|
| `Silica-<version>-uk.msi` / `-en.msi` | Installer, Ukrainian or English setup wizard. Installs to Program Files and adds a Start menu entry. |
| `Silica-<version>-portable.zip` | Unzip anywhere and run `Silica.exe`. Nothing is installed. |

Windows 10 or 11, 64-bit. The program is self-contained, so .NET does not need to
be installed. For processor temperatures and motherboard sensors install the free
[PawnIO](https://pawnio.eu) driver once; Silica tells you when it is missing.

Windows SmartScreen may warn about an unknown publisher, because the files are not
code-signed. **More info → Run anyway** starts it.

## What it does

- **Every detail of the machine.** 28 pages: processor with CPUID and instruction
  sets, motherboard and BIOS, memory modules with SPD timings, graphics, monitors with
  EDID, drives with health and wear, network, audio, input and USB devices, power and
  battery, the operating system, processes, services, startup entries, scheduled
  tasks, installed programs, drivers, updates, security (Defender, firewall, TPM,
  BitLocker, VBS), users, environment, shares, fonts and the event log
- **Live sensors** — temperatures, clocks, voltages, power, fans and loads with
  minimum, maximum and average, sparklines and charts built by clicking sensors
- **Benchmarks** — memory read, write, copy and latency, integer, compression, AES,
  SHA-256, FP32 and FP64, kept in a history and comparable with another computer
- **Disk benchmark** — sequential and random speed with the Windows cache bypassed
- **Stability test** — processor, floating point, cache, memory and disk loads where
  every result is verified, so an unstable overclock shows up as errors, not as a
  crash an hour later
- **Monitor test**, **reports** (HTML, text, Markdown, JSON), a **tray icon**, an
  always-on-top **overlay**, **CSV logging** and **alerts** when a value crosses a limit

| | |
|---|---|
| ![Sensors](docs/screenshots/sensors.png) | ![Processor](docs/screenshots/processor.png) |
| ![Benchmarks](docs/screenshots/benchmarks.png) | ![Search](docs/screenshots/search.png) |

## Why it stays light

A monitoring tool that eats a quarter of a gigabyte to show you how much memory
is free is a bad joke. Three decisions keep Silica small:

**Sensors run in a separate process.** The sensor library and its kernel driver
hold native memory that a .NET process cannot give back once it has loaded them.
So sensors live in a second `Silica.exe` that the window talks to over a named
pipe. When nothing needs live values for a minute — no sensor page open, no tray
value, no overlay, logging or alert — that process exits and its memory goes with it.

**The interface is drawn by the processor on ordinary screens.** WPF's GPU path
keeps a Direct3D device and its buffers alive for the window's lifetime, about
50 MB. Up to 2560×1600 software rendering is just as smooth, so that is the default;
on 4K screens the graphics card takes over. **Settings → Appearance → Drawing**
overrides either way.

**Nothing runs until you look at it.** Information pages are read when opened.
A minimized window stops its timers and hands its working set back to Windows.

| | Before | Now |
|---|---|---|
| Overview with live sensors | 233 MB | 152 MB window + 123 MB sensor process |
| Information page, sensors released | 228 MB | 149 MB, one process |
| Minimized | — | 7–17 MB |

*Working set, AMD Ryzen 7 7700 + RTX 5070, Windows 11.*

## Search that forgives

One box finds pages, settings, sensors and any value on any page — in every
installed language at once. It is built for people who do not know the right word:

- **typos and partial words** — optimal-string-alignment distance, so swapped
  letters cost one edit, not two
- **transliteration** — `videokarta` finds "Відеокарта"
- **wrong keyboard layout** — `ghjwtcjh` typed on an English layout finds "процесор";
  the layouts are read from the ones installed in Windows
- **synonyms** — "GPU", "videocard" and "відеокарта" lead to the same page

## Languages

52 languages are built in, and Silica follows the Windows display language on first
start. English and Ukrainian are written by hand; the others are machine-assisted and
corrections from native speakers are welcome. Arabic, Hebrew and Persian use a
right-to-left layout with numbers and units kept in reading order. Any text missing
from a language falls back to English, so a partial translation is still usable.

For fun there are three Ukrainian variants — Pirate, Viking and Cossack. A variant is
an ordinary language file with `"base": "uk"`: it borrows Ukrainian number formats and
any string it leaves out, and is never picked automatically.

A language is one JSON file; no code changes. **Settings → Language → Create
translation template**, translate the values, drop the file into the languages folder.
Rules for contributors are in [tools/TRANSLATING.md](tools/TRANSLATING.md).

## Administrator rights

Temperatures, fan speeds and drive health are not readable without them, so Silica
asks at start. Declining is fine: it runs in a limited mode and offers a restart
later.

Running elevated has consequences that are handled on purpose. Files, folders and
links open through your own Explorer, so a browser started from Silica never
inherits its rights. **Start with Windows** creates a task that starts Silica
elevated without a prompt, so it is only offered for the installed copy in Program
Files — a portable `Silica.exe` in Downloads could be replaced by any program.

## Building

```powershell
dotnet build Silica.sln                 # debug build
.\build.ps1 -Version 1.0.0              # portable zip and both installers in .\artifacts
```

Needs the .NET 9 SDK; the release script also uses Python 3 and restores WiX as a
local tool.

```powershell
Silica.exe --report report.html         # write a report without opening a window
Silica.exe --lang uk-x-pirate           # start in a given language
```

```
src/Silica.Core   information pages, sensors, benchmarks, stability test, reports (no UI)
src/Silica        WPF application
installer         WiX installer
tools             language tooling, icon generator
```

## Privacy

Silica makes no network connections. Reports, logs and benchmark results stay on
your computer unless you share them.

## License

MIT, see [LICENSE](LICENSE). Sensor access uses
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
(MPL 2.0) and the [PawnIO](https://pawnio.eu) driver.

---

**Chekaliuk Dmytro** · [@ifoxp](https://github.com/ifoxp) ·
[ifoxp.top](https://ifoxp.top) · Telegram [@ifoxp](https://t.me/ifoxp) ·
Discord `ifoxp`
