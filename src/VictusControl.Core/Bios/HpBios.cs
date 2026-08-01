// Low-level transport for HP's WMI BIOS control interface.
// Protocol reproduced (trimmed, re-namespaced) from the OmenMon project
// (https://omenmon.github.io/, GPL-3.0) under the terms of that license.
// See THIRD_PARTY_NOTICES.md at the repo root.

using System;
using System.Runtime.InteropServices;
using Microsoft.Management.Infrastructure;

namespace VictusControl.Core.Bios {

    public class BiosException : Exception {
        public BiosException(string message) : base(message) { }
    }

    public class HpBios : HpBiosData, IDisposable {

        public bool IsInitialized { get; private set; }

        private CimSession _session;
        private CimInstance _biosData;
        private CimInstance _biosMethods;

        // Telemetry is polled on a background thread while the UI thread sends
        // commands, so calls must not overlap: the CIM objects are shared and the
        // BIOS itself answers one request at a time.
        private readonly object _gate = new();

        private static readonly Lazy<HpBios> _instance = new(() => new HpBios());
        public static HpBios Instance => _instance.Value;

        private HpBios() { }

        public void Initialize() {
            if (IsInitialized) return;

            _session = CimSession.Create(null);

            _biosData = new CimInstance(_session.GetClass(BIOS_NAMESPACE, BIOS_DATA));
            _biosData.CimInstanceProperties["Sign"].Value = Sign;

            var methodsTemplate = new CimInstance(BIOS_METHOD_CLASS, BIOS_NAMESPACE);
            methodsTemplate.CimInstanceProperties.Add(
                CimProperty.Create("InstanceName", BIOS_METHOD_INSTANCE, CimFlags.Key));
            _biosMethods = _session.GetInstance(BIOS_NAMESPACE, methodsTemplate);

            IsInitialized = true;
        }

        public void Close() {
            if (!IsInitialized) return;
            IsInitialized = false;
            _biosData?.Dispose();
            _biosMethods?.Dispose();
            _session?.Dispose();
        }

        public void Dispose() => Close();

        public int Send(Cmd command, uint commandType, byte[] inData, byte outDataSize, out byte[] outData) {
            lock (_gate) {
                return SendLocked(command, commandType, inData, outDataSize, out outData);
            }
        }

        private int SendLocked(Cmd command, uint commandType, byte[] inData, byte outDataSize, out byte[] outData) {
            outData = new byte[outDataSize];
            if (!IsInitialized) Initialize();

            using var input = new CimInstance(_biosData);
            input.CimInstanceProperties["Command"].Value = command;
            input.CimInstanceProperties["CommandType"].Value = commandType;

            if (inData == null) {
                input.CimInstanceProperties["Size"].Value = 0;
            } else {
                input.CimInstanceProperties[BIOS_DATA_FIELD].Value = inData;
                input.CimInstanceProperties["Size"].Value = inData.Length;
            }

            var methodParams = new CimMethodParametersCollection();
            methodParams.Add(CimMethodParameter.Create("InData", input, CimType.Instance, CimFlags.In));

            using CimMethodResult result = _session.InvokeMethod(
                _biosMethods, BIOS_METHOD + Convert.ToString(outDataSize), methodParams);

            using var resultData = result.OutParameters["OutData"].Value as CimInstance;

            if (outDataSize != 0)
                outData = resultData.CimInstanceProperties["Data"].Value as byte[];

            return Convert.ToInt32(resultData.CimInstanceProperties[BIOS_RETURN_CODE_FIELD].Value);
        }

        public int Send(Cmd command, uint commandType, byte[] inData) {
            return Send(command, commandType, inData, 0, out _);
        }

        public static void Check(int code) {
            switch (code) {
                case 0: return;
                case -1: throw new BiosException("BIOS call failed (client-side exception).");
                case 3: throw new BiosException("BIOS command not available on this model.");
                case 5: throw new BiosException("Insufficient input/output buffer size.");
                default: throw new BiosException($"Unknown BIOS error code {code}.");
            }
        }

        // Serializes a struct into a byte array (little-endian, sequential layout)
        public static byte[] ToBytes<T>(T data) where T : struct {
            int size = Marshal.SizeOf(data);
            byte[] result = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try {
                Marshal.StructureToPtr(data, ptr, false);
                Marshal.Copy(ptr, result, 0, size);
            } finally {
                Marshal.FreeHGlobal(ptr);
            }
            return result;
        }
    }
}
