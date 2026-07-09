using System;
using System.Buffers.Binary;

namespace ModbusLibrary.Protocol
{
    public static class ModbusFrameDecoder
    {
        private static void CheckException(byte[] response)
        {
            if ((response[7] & 0x80) != 0)
            {
                byte exceptionCode = response[8];
                string message = exceptionCode switch
                {
                    0x01 => "Illegal Function",
                    0x02 => "Illegal Data Address",
                    0x03 => "Illegal Data Value",
                    0x04 => "Slave Device Failure",
                    _ => $"Unknown Error: 0x{exceptionCode:X2}"
                };
                throw new ModbusException(exceptionCode, message);
            }
        }

        // For registers (03 and 04)
        public static ushort[] DecodeReadRegistersResponse(byte[] response, byte expectedFunctionCode)
        {
            CheckException(response);
            if (response[7] != expectedFunctionCode) throw new Exception("Unexpected function code received.");

            int registerCount = response[8] / 2;
            ushort[] results = new ushort[registerCount];
            for (int i = 0; i < registerCount; i++)
            {
                results[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(9 + (i * 2), 2));
            }
            return results;
        }

        // For digital data (Bit/Coil) (01 and 02)
        public static bool[] DecodeReadBitsResponse(byte[] response, byte expectedFunctionCode, ushort quantity)
        {
            CheckException(response);
            if (response[7] != expectedFunctionCode) throw new Exception("Unexpected function code received.");

            bool[] results = new bool[quantity];
            for (int i = 0; i < quantity; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                // We check the specific bit within the byte
                results[i] = (response[9 + byteIndex] & (1 << bitIndex)) != 0;
            }
            return results;
        }

        public static void DecodeWriteResponse(byte[] response, byte expectedFunctionCode, ushort expectedAddress)
        {
            CheckException(response);
            if (response[7] != expectedFunctionCode) throw new Exception("Unexpected function code received.");

            ushort returnedAddress = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(8, 2));
            if (returnedAddress != expectedAddress) throw new Exception("The confirmation address returned by the device is incorrect.");
        }
        public static void DecodeWriteMultipleRegistersResponse(byte[] response, ushort expectedAddress)
        {
            CheckException(response);
            if (response[7] != (byte)FunctionCode.WriteMultipleRegisters) throw new Exception("Unexpected function code received.");

            ushort returnedAddress = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(8, 2));
            if (returnedAddress != expectedAddress) throw new Exception("The confirmation address returned by the device is incorrect.");
        }
    }
}