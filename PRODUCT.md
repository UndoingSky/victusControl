# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows-desktop

> Deviation from the standard schema values (`web` / `ios` / `android` / `adaptive`):
> this is a native Windows desktop application (WPF, .NET 8). None of the four
> enum values describe it, and recording `web` would be false. Native-platform
> guidance for iOS/Android does not apply; Windows desktop conventions do.

## Stack

Existing codebase: C# / .NET 8, WPF for UI, split into `VictusControl.Core`
(hardware access), `VictusControl.App` (WPF UI), and `VictusControl.Diag`
(hardware probe tool). Chosen because HP's control interface is reached through
WMI, which .NET addresses directly.

## Users

A single primary user today: the owner of an HP Victus gaming laptop (Intel
i5 13th gen, RTX 4050, 16 GB RAM) who runs Omen Gaming Hub's job without Omen
Gaming Hub — it carries ads and feels slow. Intended to widen to other Victus
and Omen owners on release.

The app lives minimized in the system tray during normal use. It is summoned
deliberately, and when it appears the user wants the full machine readout at
once rather than a single number.

## Product Purpose

Monitor and control the laptop's thermal and performance hardware: fan speed
and mode, performance/power profiles, and live CPU/GPU/fan telemetry. Success
is a tool that opens instantly, shows the truth about the machine's state, and
applies a change without ceremony — the qualities the vendor software lacks.

## Positioning

Direct, ad-free access to the same HP BIOS/WMI control surface Omen Gaming Hub
uses, in an application that costs nothing to open and nothing to trust. Its
advantage is subtraction: no advertising, no account, no background service
suite, and no latency between intent and effect.

## Operating Context

- Runs on Windows 11, tray-resident, launched at the user's initiative.
- **Requires administrator elevation** — the HP BIOS WMI interface refuses
  unelevated callers. This is a permanent condition of the product, not a bug.
- Used alongside games running full-screen; the window is summoned and dismissed
  rather than lived in.
- Telemetry is polled on a timer, so every displayed value is a recent sample
  rather than a continuous reading.

## Capabilities and Constraints

Confirmed working on the target hardware (verified by probing the machine, not
assumed from documentation):

- **Fan telemetry and control.** Fan level is reported and accepted in units of
  100 RPM. Measured ceilings on this machine: CPU 54 (5400 rpm), GPU 52
  (5200 rpm).
- **Fan modes.** Default, Performance, Cool, Quiet all apply successfully.
- **CPU temperature** comes from the ACPI thermal zone via WMI. HP's BIOS
  temperature command (`0x23`) returns success but all-zero data on this model
  and must not be used.
- **CPU usage** must be read from `Processor Information\% Processor Utility`;
  the older `% Processor Time` counter measures against base clock and reads
  low whenever the CPU boosts.
- **GPU telemetry** comes from `nvidia-smi`, which ships with the NVIDIA driver.
- **GPU power levels** (Minimum / Medium / Maximum TGP) apply successfully.

Confirmed unavailable:

- **GPU mode / MUX switching is not supported on this machine.** The BIOS
  returns error 4. No software can enable it; the control must be shown as
  unavailable rather than offered and failed.

Known behavioral limits:

- Manual fan levels are reclaimed by the BIOS fan curve after a few seconds.
  Holding a manual speed requires periodic re-assertion; a custom fan curve
  feature depends on this.
- Fan RPM changes lag mode changes — at idle a mode switch may produce no
  audible or numeric change until the machine heats up. The interface must not
  imply a change failed when it merely has not taken effect yet.

## Brand Commitments

Name: **Victus Control**. Logo: `victus.jpg`, a chevron "V" mark, converted to
`src/VictusControl.App/victus.ico` for the window, tray and executable.

**Standing visual preference (user decision):** the interface follows the
category convention — a dark sidebar-and-cards control panel in the manner of
OMEN Gaming Hub, which the user named as the quality bar. An earlier build used
a deliberately unconventional one-bit HyperCard treatment; the user replaced it,
and that decision stands. Future work executes this convention at full fidelity
rather than reintroducing an alternative visual world.

Dark is the default theme with a light theme available; the choice persists
between runs. Accent is a violet-to-magenta ramp, and status colours (green /
amber / red) carry meaning and therefore never change with the theme.

## Evidence on Hand

- `THIRD_PARTY_NOTICES.md` — the HP WMI protocol constants derive from the
  OmenMon project (GPL-3.0), which makes the core library a derivative work.
  **Release must therefore be GPL-3.0.**
- `src/VictusControl.Diag/` — a working hardware probe; its saved output is the
  source of every hardware claim above.
- No users, benchmarks, testimonials, download counts, or press exist. Future
  work must not fabricate them.

## Product Principles

1. **Report the machine honestly.** Never display a fabricated or placeholder
   value. An unavailable reading says so; a number shown is a number measured.
2. **Unsupported is not the same as broken.** Hardware this machine lacks is
   disclosed and disabled, never offered as a control that silently fails.
3. **Acknowledge every command.** The user must always know whether an action
   reached the hardware — the absence of feedback is the vendor software's
   failure, not a style to inherit.
4. **Earn the open window.** The app is summoned from the tray for a reason;
   it must deliver the full state of the machine immediately, without
   navigation.
5. **Cost nothing to run.** No ads, no account, no telemetry upload, and a
   polling loop light enough to leave running during a game.

## Accessibility & Inclusion

No user-specific requirement established. The operating context does set a
floor: readable at a glance while alt-tabbed from a game, which makes contrast
and numeric legibility functional requirements rather than preferences.
