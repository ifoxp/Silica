# Silica — project rules

Open-source system information, monitoring and benchmarking tool for Windows (own implementation inspired by what AIDA64 does; no code copied). C# / .NET 9, WPF.

## Product rules (from the owner, non-negotiable)

1. No emoji anywhere: UI, strings, reports, logs, comments, commit messages. The product must have a style of its own.
2. No "simple-looking" or template UI. Every screen is designed: hierarchy, spacing, states (loading, empty, error, no-admin), keyboard and mouse behaviour.
3. Verify every change for real: build, run the app, look at the result (screenshots), check behaviour and logs. No known bugs, no confusing log output, nothing half-working.
4. Lean but not cheap: assets are optimized (vector icons where possible, compressed raster only when needed), yet look sharp from a 14" laptop to a 4K monitor. The app icon must read well at 16 px and at 256 px.
5. Ukrainian and English are always supported and complete. The language system must make adding a new language a matter of adding one file (no code changes), with a way to see missing keys.
6. Code and product must not look AI-generated: comments only where something is genuinely non-obvious, short and sparse; no narrating comments, no banner/summary comments, no doc comments on every member, no filler text in the UI.

7. Resource-light: nothing runs unless the user needs it. No load spike on startup; pages are collected on demand, sensors are polled only while something displays or logs them, and every background feature (tray, overlay, logging, alerts, search pre-indexing) can be switched off.
8. Search must be smart and forgiving: works in any installed language at once, tolerates typos, partial words, transliteration and wrong keyboard layout, knows synonyms ("відеокарта" = "GPU" = "videocard").

## Decisions

- Name: **Silica**. Elevation: UAC prompt at start (requireAdministrator); if refused, the app still works in a limited mode with a clear notice.
- Distribution: portable single exe (self-contained) plus an installer. Settings in %AppData%\Silica, or next to the exe in portable mode.
- v1 scope: all info pages, sensors with charts, dashboard, benchmarks (CPU/FPU/memory/disk) with history and comparison, stability test with error detection, reports (HTML/TXT/MD/JSON), smart search, themes, UA/EN, tray icon + overlay panel, sensor logging (CSV) + alerts, monitor test.

## Git

- Commits are authored by the owner only: never add Co-Authored-By or any Claude/AI attribution.
- Never push; the owner pushes to GitHub.
