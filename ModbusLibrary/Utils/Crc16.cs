using System;

namespace ModbusLibrary.Utils
{
    public static class Crc16
    {
        /// <summary>
        /// Calculates the Modbus RTU CRC16 value for a given byte array.
        /// </summary>
        /// <param name="data">The byte array to calculate the CRC for.</param>
        /// <returns>A 2-byte array containing the CRC in Little-Endian format.</returns>
        public static byte[] Calculate(byte[] data)
        {
            ushort crc = 0xFFFF; // Initial value for Modbus CRC16

            for (int pos = 0; pos < data.Length; pos++)
            {
                crc ^= (ushort)data[pos]; // XOR byte into least sig. byte of crc

                for (int i = 8; i != 0; i--) // Loop over each bit
                {
                    // If the LSB is set
                    if ((crc & 0x0001) != 0)
                    {
                        crc >>= 1; // Shift right
                        crc ^= 0xA001; // XOR with polynomial
                    }
                    else // Else LSB is not set
                    {
                        crc >>= 1; // Just shift right
                    }
                }
            }

            // Modbus RTU requires the CRC to be appended as Low-Byte first, then High-Byte.
            return new byte[] { (byte)(crc & 0xFF), (byte)(crc >> 8) };
        }

        /// <summary>
        /// Validates if the given frame has a correct CRC16 signature.
        /// </summary>
        /// <param name="frame">The full received frame including the 2-byte CRC at the end.</param>
        /// <returns>True if CRC matches, False otherwise.</returns>
        public static bool IsValid(byte[] frame)
        {
            if (frame == null || frame.Length < 4)
                return false; // Minimum frame size (UnitID + FuncCode + CRC) is 4 bytes

            // Extract the payload (everything except the last 2 bytes)
            byte[] payload = new byte[frame.Length - 2];
            Array.Copy(frame, 0, payload, 0, payload.Length);

            // Calculate what the CRC should be
            byte[] calculatedCrc = Calculate(payload);

            // Compare with the actual CRC attached to the frame
            return frame[frame.Length - 2] == calculatedCrc[0] &&
                   frame[frame.Length - 1] == calculatedCrc[1];
        }
    }
}
