// Definitions of HP's undocumented BIOS/WMI control interface (fan, GPU mode, CPU/GPU power).
// The command IDs, structure layouts and enum values below are not published by HP anywhere;
// they were reverse-engineered by the OmenMon project (https://omenmon.github.io/, GPL-3.0)
// and are reproduced here (trimmed, re-namespaced, no localization/config dependency)
// under the terms of the GPL-3.0 license. See THIRD_PARTY_NOTICES.md at the repo root.

using System;
using System.Runtime.InteropServices;

namespace VictusControl.Core.Bios {

    public abstract class HpBiosData {

        // Shared secret HP's WMI BIOS interface expects on every call ("SECU" in ASCII)
        protected static readonly byte[] Sign = { 0x53, 0x45, 0x43, 0x55 };

        public enum Cmd : uint {
            Default  = 0x20008,
            Keyboard = 0x20009,
            Legacy   = 0x00001,
            GpuMode  = 0x00002
        }

        protected const string BIOS_DATA = "hpqBDataIn";
        protected const string BIOS_DATA_FIELD = "hpqBData";
        protected const string BIOS_METHOD = "hpqBIOSInt";
        protected const string BIOS_METHOD_CLASS = "hpqBIntM";
        protected const string BIOS_METHOD_INSTANCE = "ACPI\\PNP0C14\\0_0";
        protected const string BIOS_NAMESPACE = "root\\wmi";
        protected const string BIOS_RETURN_CODE_FIELD = "rwReturnCode";

        public enum FanMode : byte {
            Default     = 0x30,
            Performance = 0x31,
            Cool        = 0x50,
            Quiet       = 0x03,
            LegacyDefault = 0x00
        }

        public enum GpuMode : byte {
            Hybrid   = 0x00,
            Discrete = 0x01,
            Optimus  = 0x02
        }

        public enum Throttling : byte {
            Unknown = 0x00,
            On      = 0x01,
            Default = 0x04
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct FanLevel {
            public byte Fan1Level, Fan2Level, Temperature;

            public FanLevel(byte[] data) {
                Fan1Level = data[0];
                Fan2Level = data[1];
                Temperature = data[2];
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct FanTable {
            public byte FanCount;
            public byte LevelCount;
            public FanLevel[] Level;

            public FanTable(byte[] data) {
                FanCount = data[0];
                LevelCount = data[1];
                Level = new FanLevel[LevelCount];
                for (int i = 0; i < LevelCount; i++) {
                    Level[i] = new FanLevel(new byte[] {
                        data[2 + 3 * i + 0],
                        data[2 + 3 * i + 1],
                        data[2 + 3 * i + 2]
                    });
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
        public struct CpuPowerData {
            public byte Limit1;       // PL1
            public byte Limit2;       // PL2
            public byte Limit4;       // PL4
            public byte LimitWithGpu; // Concurrent CPU limit shared with GPU

            public static CpuPowerData NoChange => new CpuPowerData {
                Limit1 = 0xFF, Limit2 = 0xFF, Limit4 = 0xFF, LimitWithGpu = 0xFF
            };
        }

        public enum GpuPowerLevel : byte {
            Minimum = 0x00,
            Medium  = 0x01,
            Maximum = 0x02
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
        public struct GpuPowerData {
            public byte CustomTgp;       // 0 = off, 1 = on
            public byte Ppab;            // Processing Power AI Boost: 0 = off, 1 = on
            public byte DState;          // device power state, observed 0x01
            public byte PeakTemperature;

            public GpuPowerData(GpuPowerLevel level) {
                CustomTgp = level == GpuPowerLevel.Minimum ? (byte) 0 : (byte) 1;
                Ppab = level == GpuPowerLevel.Maximum ? (byte) 1 : (byte) 0;
                DState = 0x01;
                PeakTemperature = 0;
            }

            public GpuPowerData(byte[] data) {
                CustomTgp = data[0];
                Ppab = data[1];
                DState = data[2];
                PeakTemperature = data[3];
            }
        }
    }
}
