using System;

namespace ModbusLibrary.Utils
{
    public static class ModbusDataFormatter
    {
        public static ushort ParseRegisterValue(string valueText, string selectedType)
        {
            return selectedType switch
            {
                "Signed" => (ushort)short.Parse(valueText),
                "Unsigned" => ushort.Parse(valueText),
                "Hex" => Convert.ToUInt16(valueText.Replace("0x", ""), 16),
                "Binary" => Convert.ToUInt16(valueText, 2),
                _ => throw new FormatException("Multiple register writing (16) is not supported.")
            };
        }

        public static int GetStepForDataType(string dataType)
        {
            if (dataType.Contains("Double")) return 4;
            if (dataType.Contains("Float") || dataType.Contains("Long")) return 2;
            return 1;
        }

        public static string FormatValue(ushort[] registers, int startIndex, string dataType)
        {
            try
            {
                switch (dataType)
                {
                    case "Signed": return ((short)registers[startIndex]).ToString();
                    case "Unsigned": return registers[startIndex].ToString();
                    case "Hex": return "0x" + registers[startIndex].ToString("X4");
                    case "Binary": return Convert.ToString(registers[startIndex], 2).PadLeft(16, '0');

                    case "Float":
                        if (startIndex + 1 >= registers.Length) return "N/A";
                        return BitConverter.ToSingle(GetBytesForBitConverter(registers, startIndex, 2, true), 0).ToString("0.#########");
                    case "Float inverse":
                        if (startIndex + 1 >= registers.Length) return "N/A";
                        return BitConverter.ToSingle(GetBytesForBitConverter(registers, startIndex, 2, false), 0).ToString("0.#########");

                    case "Long":
                        if (startIndex + 1 >= registers.Length) return "N/A";
                        return BitConverter.ToInt32(GetBytesForBitConverter(registers, startIndex, 2, true), 0).ToString();
                    case "Long Inverse":
                        if (startIndex + 1 >= registers.Length) return "N/A";
                        return BitConverter.ToInt32(GetBytesForBitConverter(registers, startIndex, 2, false), 0).ToString();

                    case "Double":
                        if (startIndex + 3 >= registers.Length) return "N/A";
                        return BitConverter.ToDouble(GetBytesForBitConverter(registers, startIndex, 4, true), 0).ToString("0.###############");
                    case "Double Inverse":
                        if (startIndex + 3 >= registers.Length) return "N/A";
                        return BitConverter.ToDouble(GetBytesForBitConverter(registers, startIndex, 4, false), 0).ToString("0.###############");

                    default: return registers[startIndex].ToString();
                }
            }
            catch { return "Error"; }
        }

        public static ushort[] BuildMultiRegisterValue(string valueText, string dataType)
        {
            switch (dataType)
            {
                case "Float":
                    return GetRegistersFromBytes(BitConverter.GetBytes(float.Parse(valueText)), true);
                case "Float inverse":
                    return GetRegistersFromBytes(BitConverter.GetBytes(float.Parse(valueText)), false);
                case "Long":
                    return GetRegistersFromBytes(BitConverter.GetBytes(int.Parse(valueText)), true);
                case "Long Inverse":
                    return GetRegistersFromBytes(BitConverter.GetBytes(int.Parse(valueText)), false);
                case "Double":
                    return GetRegistersFromBytes(BitConverter.GetBytes(double.Parse(valueText)), true);
                case "Double Inverse":
                    return GetRegistersFromBytes(BitConverter.GetBytes(double.Parse(valueText)), false);
                default:
                    throw new FormatException($"Unsupported data type: {dataType}");
            }
        }

        private static byte[] GetBytesForBitConverter(ushort[] registers, int startIndex, int registerCount, bool inverseWords)
        {
            byte[] result = new byte[registerCount * 2];
            for (int i = 0; i < registerCount; i++)
            {
                int regIndex = startIndex + (inverseWords ? (registerCount - 1 - i) : i);
                ushort regValue = registers[regIndex];
                int byteIndex = (registerCount - 1 - i) * 2;
                result[byteIndex] = (byte)(regValue & 0xFF);
                result[byteIndex + 1] = (byte)((regValue >> 8) & 0xFF);
            }
            return result;
        }

        private static ushort[] GetRegistersFromBytes(byte[] bytes, bool inverseWords)
        {
            int registerCount = bytes.Length / 2;
            ushort[] registers = new ushort[registerCount];

            for (int i = 0; i < registerCount; i++)
            {
                int byteIndex = (registerCount - 1 - i) * 2;
                ushort value = (ushort)(bytes[byteIndex] | (bytes[byteIndex + 1] << 8));
                int regIndex = inverseWords ? (registerCount - 1 - i) : i;
                registers[regIndex] = value;
            }

            return registers;
        }
    }
}