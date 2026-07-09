using System;
using System.Buffers.Binary;

namespace ModbusLibrary.Protocol
{
    public static class ModbusFrameEncoder
    {
        // Functions 01, 02, 03, and 04 all have the same request packet structure. We solve it with a single method.
        public static byte[] EncodeReadRequest(ushort transactionId, byte unitId, byte functionCode, ushort startAddress, ushort quantity)
        {
            MbapHeader header = new MbapHeader { TransactionId = transactionId, ProtocolId = 0, Length = 6, UnitId = unitId };
            byte[] frame = new byte[12];
            Array.Copy(header.ToByteArray(), 0, frame, 0, 7);

            frame[7] = functionCode;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8, 2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10, 2), quantity);

            return frame;
        }

        // 06 Write Single Register
        public static byte[] EncodeWriteSingleRegisterRequest(ushort transactionId, byte unitId, ushort registerAddress, ushort value)
        {
            MbapHeader header = new MbapHeader { TransactionId = transactionId, ProtocolId = 0, Length = 6, UnitId = unitId };
            byte[] frame = new byte[12];
            Array.Copy(header.ToByteArray(), 0, frame, 0, 7);

            frame[7] = (byte)FunctionCode.WriteSingleRegister;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8, 2), registerAddress);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10, 2), value);

            return frame;
        }

        // 05 Write Single Coil
        public static byte[] EncodeWriteSingleCoilRequest(ushort transactionId, byte unitId, ushort coilAddress, bool value)
        {
            MbapHeader header = new MbapHeader { TransactionId = transactionId, ProtocolId = 0, Length = 6, UnitId = unitId };
            byte[] frame = new byte[12];
            Array.Copy(header.ToByteArray(), 0, frame, 0, 7);

            frame[7] = (byte)FunctionCode.WriteSingleCoil;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8, 2), coilAddress);

            // According to the Modbus standard, 0xFF00 is sent if the coil is ON, and 0x0000 is sent if it is OFF.
            ushort coilValue = (ushort)(value ? 0xFF00 : 0x0000);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10, 2), coilValue);

            return frame;
        }
        public static byte[] EncodeWriteMultipleRegistersRequest(ushort transactionId, byte unitId, ushort startAddress, ushort[] values)
        {
            int registerCount = values.Length;
            int byteCount = registerCount * 2;
            int length = 7 + byteCount; // UnitId + FuncCode + Address + Quantity + ByteCount + Data

            MbapHeader header = new MbapHeader
            {
                TransactionId = transactionId,
                ProtocolId = 0,
                Length = (ushort)(length), // MBAP length does not count UnitId
                UnitId = unitId
            };

            byte[] frame = new byte[6 + length];
            Array.Copy(header.ToByteArray(), 0, frame, 0, 7);

            frame[7] = (byte)FunctionCode.WriteMultipleRegisters; // 0x10 = 16
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8, 2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10, 2), (ushort)registerCount);
            frame[12] = (byte)byteCount;

            for (int i = 0; i < registerCount; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(13 + (i * 2), 2), values[i]);
            }

            return frame;
        }
    }
}
