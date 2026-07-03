using System;
using System.Buffers.Binary;

namespace ModbusLibrary.Protocol
{
    public struct MbapHeader
    {
        public ushort TransactionId;
        public ushort ProtocolId;
        public ushort Length;
        public byte UnitId;

        public byte[] ToByteArray()
        {
            byte[] headerBytes = new byte[7];

            BinaryPrimitives.WriteUInt16BigEndian(headerBytes.AsSpan(0, 2), TransactionId);
            BinaryPrimitives.WriteUInt16BigEndian(headerBytes.AsSpan(2, 2), ProtocolId);
            BinaryPrimitives.WriteUInt16BigEndian(headerBytes.AsSpan(4, 2), Length);
            headerBytes[6] = UnitId;

            return headerBytes;
        }
    }
}
