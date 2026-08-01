# Victus Control

Direct access to your HP Victus or Omen laptop's hardware controls. No ads, no account, no bloat. Just real-time monitoring and fan control in a lightweight system tray application.

## Why Victus Control?

Omen Gaming Hub provides access to your laptop's thermal and performance settings, but it includes ads, requires an account, runs background services, and adds latency between you and your hardware. Victus Control strips all that away.

**What you get:**
- Direct access to HP BIOS controls via WMI
- Real-time CPU, GPU, memory, storage, and network telemetry
- Fan mode and speed control
- GPU power level adjustment
- Minimal resource footprint (runs safely during gaming)
- Dark and light themes
- Honest reporting (unsupported features are clearly marked unavailable)

## Getting started

### Requirements

- Windows 11 on an HP Victus or Omen laptop
- Administrator privileges (required for fan and power controls)
- NVIDIA driver (for GPU telemetry on systems with NVIDIA GPUs)

### Installation

1. Clone this repository
2. Open `VictusControl.sln` in Visual Studio 2022 or later
3. Build the solution
4. Run `VictusControl.App` with administrator privileges

Or from the command line:

```powershell
dotnet build VictusControl.sln
cd src\VictusControl.App\bin\Debug\net8.0-windows
.\VictusControl.App.exe
```

### First run

When you first launch Victus Control, it will attempt to connect to your laptop's BIOS interface via WMI. If the connection fails, you will see a message. Administrator elevation is permanent requirement because the HP BIOS WMI interface will not respond to unelevated callers.

The app lives in your system tray when minimized. Click the tray icon to bring it back, or close it entirely when you are done.

## Usage

### Monitoring tab

Displays live system telemetry: CPU usage and temperature, GPU usage and temperature, RAM, storage, network, and any running games.

### Performance tab

Adjust fan modes (Default, Performance, Cool, Quiet) and GPU power levels (Minimum, Medium, Maximum TGP). Max Fan mode forces maximum cooling when active.

Note: Manual fan levels are reclaimed by the BIOS fan curve after a few seconds. The interface must not imply a change failed when it merely has not taken effect yet.

### Games tab

Tracks games you have launched. Game detection runs in the background even when the app is minimized in the tray.

### Settings

Theme preference (dark/light) is saved between runs. All other settings are read-only hardware queries.

## Screenshots

![System vitals in dark mode](screenshots/system_vitals_dark.png)

![System vitals in light mode](screenshots/system_vitals_light.png)

![Performance controls](screenshots/performance_control.png)

![Games section](screenshots/games_section.png)

## Project structure

- `src/VictusControl.App`: WPF user interface, system tray integration, and theme management
- `src/VictusControl.Core`: BIOS access, system monitoring (CPU, GPU, memory, network), and game tracking
- `src/VictusControl.Diag`: Standalone diagnostic tool for probing hardware capabilities

## Development

### Building

```powershell
dotnet build VictusControl.sln
```

### Supported hardware

Developed and tested on:
- HP Victus with Intel i5 13th gen and RTX 4050

Other Victus and Omen laptops may have varying levels of hardware support. Use `VictusControl.Diag` to probe your specific machine.

### Known limitations

- GPU mode/MUX switching is not supported on all hardware (shows as unavailable, never offers a control that will fail silently)
- Manual fan levels are reclaimed by the BIOS curve after a few seconds
- Fan RPM changes lag behind mode changes at idle
- GPU telemetry requires the NVIDIA driver

## Troubleshooting

### BIOS interface unavailable

If you see "BIOS interface unavailable" on startup:
1. Restart the app with administrator privileges
2. Your laptop may not support WMI-based BIOS access (uncommon on Victus/Omen)
3. Run `VictusControl.Diag` to probe your hardware

### Fans are not responding to controls

- Manual fan levels are temporary and will be reclaimed by the BIOS fan curve within seconds
- Ensure you have administrator privileges
- Try switching to a different fan mode and back again

### GPU stats show as unavailable

- Verify your NVIDIA driver is installed and up to date
- GPU power level controls only work on systems that support them

### Performance

Victus Control is designed to be lightweight. Typical memory usage is under 50 MB. When the window is minimized, polling intervals increase to reduce CPU usage.

## License

This project is distributed under the GPL 3.0 license. See THIRD_PARTY_NOTICES.md for third party attribution, including the OmenMon project (GPL 3.0) which provided the HP BIOS protocol constants.
