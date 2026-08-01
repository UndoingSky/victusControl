// High-level HP BIOS control operations (fan speed, performance mode, GPU mode, power limits).
// Command sequences reproduced (trimmed, re-namespaced) from the OmenMon project
// (https://omenmon.github.io/, GPL-3.0) under the terms of that license.
// See THIRD_PARTY_NOTICES.md at the repo root.

using System;

namespace VictusControl.Core.Bios {

    public class HpBiosControl {

        private readonly HpBios _bios = HpBios.Instance;

        // Fan levels are reported/accepted in units of 100 RPM.
        // Ceilings below were measured on this machine (HP Victus, i5-13th gen / RTX 4050)
        // by engaging max-fan mode and sampling until the ramp plateaued.
        public const byte MaxCpuFanLevel = 54; // 5400 RPM
        public const byte MaxGpuFanLevel = 52; // 5200 RPM

        public static int LevelToRpm(byte level) => level * 100;

        public void Initialize() => _bios.Initialize();

        // --- Fans ---

        public byte GetFanCount() {
            HpBios.Check(_bios.Send(HpBiosData.Cmd.Default, 0x10, new byte[4], 4, out var outData));
            return outData[0];
        }

        // Returns { cpuFanLevel, gpuFanLevel } as raw BIOS units (not RPM)
        public byte[] GetFanLevel() {
            HpBios.Check(_bios.Send(HpBiosData.Cmd.Default, 0x2D, new byte[4], 128, out var outData));
            return new[] { outData[0], outData[1] };
        }

        // level: raw BIOS fan level for CPU and GPU fan
        public void SetFanLevel(byte cpuLevel, byte gpuLevel) {
            HpBios.Check(_bios.Send(HpBiosData.Cmd.Default, 0x2E,
                new byte[4] { cpuLevel, gpuLevel, 0x00, 0x00 }));
        }

        public void SetFanMode(HpBiosData.FanMode mode) {
            HpBios.Check(_bios.Send(HpBiosData.Cmd.Default, 0x1A,
                new byte[4] { 0xFF, (byte)mode, 0x00, 0x00 }));
        }

        public bool GetMaxFan() {
            HpBios.Check(_bios.Send(HpBiosData.Cmd.Default, 0x26, new byte[4], 4, out var outData));
            return (outData[0] & 1) != 0;
        }

        public void SetMaxFan(bool enabled) {
            HpBios.Check(_bios.Send(HpBiosData.Cmd.Default, 0x27,
                new byte[4] { (byte)(enabled ? 1 : 0), 0, 0, 0 }));
        }

        // BIOS thermal sensor. Note: on Victus models this call succeeds (rc=0) but
        // always returns 0, so SystemMonitor.GetCpuTemperatureC() (ACPI thermal zone)
        // is the reliable source. Kept for models where it is actually populated.
        public byte GetTemperature() {
            HpBios.Check(_bios.Send(HpBiosData.Cmd.Default, 0x23,
                new byte[4] { 0x01, 0x00, 0x00, 0x00 }, 4, out var outData));
            return outData[0];
        }

        public HpBiosData.FanTable GetFanTable() {
            HpBios.Check(_bios.Send(HpBiosData.Cmd.Default, 0x2F, new byte[4], 128, out var outData));
            return new HpBiosData.FanTable(outData);
        }

        // --- GPU mode (requires reboot to take effect) ---

        // True if this machine has a switchable MUX. Victus RTX 4050 units generally
        // do not: the query returns BIOS error 4 and GPU mode cannot be changed.
        public bool IsGpuModeSupported() {
            int rc = _bios.Send(HpBiosData.Cmd.Legacy, 0x52, null!, 4, out _);
            return rc == 0;
        }

        public HpBiosData.GpuMode GetGpuMode() {
            // Returns BIOS error on unsupported devices; treat as Hybrid (0) in that case
            _bios.Send(HpBiosData.Cmd.Legacy, 0x52, null!, 4, out var outData);
            return (HpBiosData.GpuMode)outData[0];
        }

        public void SetGpuMode(HpBiosData.GpuMode mode) {
            HpBios.Check(_bios.Send(HpBiosData.Cmd.GpuMode, 0x52,
                new byte[4] { (byte)mode, 0x00, 0x00, 0x00 }));
        }

        public HpBiosData.GpuPowerData GetGpuPower() {
            HpBios.Check(_bios.Send(HpBiosData.Cmd.Default, 0x21, new byte[4], 4, out var outData));
            return new HpBiosData.GpuPowerData(outData);
        }

        public void SetGpuPower(HpBiosData.GpuPowerLevel level) {
            var data = new HpBiosData.GpuPowerData(level);
            HpBios.Check(_bios.Send(HpBiosData.Cmd.Default, 0x22, HpBios.ToBytes(data)));
        }

        // --- CPU power limits ---

        public void SetCpuPower1(byte watts) {
            var data = HpBiosData.CpuPowerData.NoChange;
            data.Limit1 = watts;
            data.Limit2 = watts;
            HpBios.Check(_bios.Send(HpBiosData.Cmd.Default, 0x29, HpBios.ToBytes(data)));
        }

        public void SetCpuPower4(byte watts) {
            var data = HpBiosData.CpuPowerData.NoChange;
            data.Limit4 = watts;
            HpBios.Check(_bios.Send(HpBiosData.Cmd.Default, 0x29, HpBios.ToBytes(data)));
        }

        public HpBiosData.Throttling GetThrottling() {
            _bios.Send(HpBiosData.Cmd.Default, 0x35, new byte[4] { 0x00, 0x04, 0x00, 0x00 }, 128, out var outData);
            return (HpBiosData.Throttling)outData[1];
        }
    }
}
