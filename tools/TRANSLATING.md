# Translating Silica

A language is one JSON file in `src/Silica/Languages/<code>.json`:

```json
{
  "language": { "code": "de", "name": "Deutsch", "englishName": "German", "author": "Silica", "plural": "one-other" },
  "strings": { "<English key>": "<translation>", ... },
  "synonyms": { "<English key>": ["word people type", "..."] }
}
```

Rules:

- Keys are the English texts from `en.json`. Translate every value; never change a key.
- Keep placeholders exactly: `{0}`, `{1}`. Their order in the sentence may change.
- Plural keys (`{0} sensors`, `{0} results`, `{0} threads`, `{0} times`, `{0} cores`) hold all forms of the language's
  plural rule separated by `|`, each form containing `{0}`:

  | rule | forms |
  |---|---|
  | `none` | other |
  | `one-other` | one \| other |
  | `zero-one-other` | one (0 and 1) \| other |
  | `east-slavic`, `polish` | one \| few \| many |
  | `czech`, `lithuanian`, `romanian` | one \| few \| other |
  | `latvian` | zero \| one \| other |
  | `slovenian` | one \| two \| few \| other |
  | `arabic` | zero \| one \| two \| few \| many \| other |
  | `hebrew` | one \| two \| other |

  No other value may contain `|`.
- Unit keys (`B`, `KB`, `MB`, `GB`, `TB`, `PB`, `MHz`, `GHz`, `Hz`, `bps`, `Kbps`, `Mbps`, `Gbps`, `Tbps`, `/s`, `d`, `s`,
  `V`, `A`, `W`, `RPM`, `L/h`, `mWh`, `dBA`, `µS/cm`, `MT/s`, `MB/s`, `ns`, `points`, `Mpix/s`, `GFLOPS`) are symbols:
  use what your language normally writes (French writes `Go` for GB and `o` for B; many languages keep the Latin symbols).
  `d` is the short form of "days" in an uptime like "3 d 04:12:55"; `/s` is appended to a size ("12 MB/s").
- Technical names stay as they are: CPUID, SMART, NVMe, PCIe, D3D, BIOS, UEFI, TPM, BitLocker, DirectX, .NET, SSID,
  MAC, IPv4, memory timings (tRAS, tRCD, ...), product names (Microsoft Defender, Windows, Silica, PawnIO).
- Sensor names (`CPU Core #{0}`, `GPU Hot Spot`, ...) read like a hardware monitor would label them in your language.
- Short UI labels stay short; this is a desktop app with limited space. Use the tone of the operating system's own UI
  (polite, neutral, no exclamation marks, no emoji).
- `synonyms`: for the main page keys (Overview, Sensors, Processor, Graphics, Memory, Storage, Motherboard, Monitors,
  Network, Audio, Devices, Power, Operating system, Processes, Services, Startup, Installed programs, Drivers, Security,
  Benchmarks, Disk benchmark, Stability test, Monitor test, Report, Settings) add the words people really type when
  searching, including colloquial ones.

Check a file with `python tools/check_lang.py <code>`.

## Variants

A playful or regional variant of an existing language sets `"base"` to that language's code and uses a private-use
code, for example `"code": "uk-x-pirate", "base": "uk"`. Numbers and dates follow the base language, and any key the
variant leaves out is shown from the base language instead of English, so a variant may restyle only part of the
strings. Variants are never picked automatically from the Windows language, and their words join the search only while
the variant is in use.
