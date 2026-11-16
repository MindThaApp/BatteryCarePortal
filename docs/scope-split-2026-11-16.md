# Scope split: Power Plan Manager + Portal Battery Care (owner decision 2026-11-16)

## Why
Power Plan Manager (PPM) grew to cover power plans, battery care, fans, sensors, schedules and more.
Pro sales so far are very low. One clear pitch per app sells better than one app that does everything.

## Power Plan Manager (desktops and laptops): keeps
- **Power plans**: switching, plus the advanced/hidden power settings editor with plain-language explanations.
- **Automatic switching** by CPU load (with hysteresis/delays so plans don't flap) and by running/focused app
  (per-app rules, e.g. Game.exe -> High performance, revert on exit). This is the headline feature.
- **Plugged-in / on-battery plan switching stays in PPM** (owner, 2026-11-16): it is a power-plan rule, not
  battery care. Portal Battery Care only handles charging and battery health.
- **Fan control via FanControl (Rem0o.FanControl)**, which supports many motherboards:
  - Quiet / Balanced / Performance presets = PPM-owned FanControl config files, switched with the power plan.
  - Free-form curves under Advanced, behind a one-time warning, with Restore defaults and a 30 s keep-or-revert
    prompt that reverts automatically if unanswered.
  - Never overwrite the user's own FanControl config (`userConfig.json`). PPM writes/switches only its own files
    (e.g. `PPM-Quiet.json`) and switches back to the user's original config on exit or when disabled
    (existing "hand fans back" rule, fan-owner.json).
  - Built-in presets keep minimum fan speeds and a full-speed high-temperature step.
  - Open item: verify that a non-elevated `-c` call reaches an elevated running FanControl (fancontrol-notes.md:24).
- **Temperature/power sensors** (CPU via LibreHardwareMonitor, GPU via AMD ADL / NVML), **widgets**, hotkeys,
  schedules, presets, history, toasts, search, profile export.
- **Power flow** (kept in BOTH apps).

## Moves out of PPM -> new app "Portal Battery Care" (laptops)
- Everything battery-care related: charge limit, sailing, top-up, heat protection, calibration, battery health,
  Battery page, battery rules.
- Laptop OEM backends (Lenovo conservation via \\.\EnergyDrv working; Dell/HP/ASUS off until verified on real
  hardware or by beta testers).
- Fan control on laptops: only the manufacturer's official fan/performance modes where exposed (no custom curves,
  no raw embedded-controller writes, no third-party drivers).
- **Power flow** (shared with PPM).

## Shared
- Common core library for power flow, sensors, Store licensing/trial and settings so both apps don't duplicate code.
- Both apps: free download + in-app trial + durable Pro add-on (Files Portal model, see single-listing plan).

## Next
- Design/UI/UX reviews for both apps are being produced; act on them after the owner picks what to do.
