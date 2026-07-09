using System;

namespace ModbusLibrary.Utils
{
    public static class ModbusTrafficLogger
    {
        // The UI will subscribe to this Action. byte[] = Data, bool = Tx (true) Rx (false)
        public static Action<byte[], bool>? OnTraffic;

        public static void LogTx(byte[] data) => OnTraffic?.Invoke(data, true);
        public static void LogRx(byte[] data) => OnTraffic?.Invoke(data, false);
    }
}
