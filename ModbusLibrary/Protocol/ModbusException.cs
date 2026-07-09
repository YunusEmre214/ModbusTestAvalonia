using System;
using System.Collections.Generic;
using System.Text;



namespace ModbusLibrary.Protocol
{
    public class ModbusException : Exception
    {
        public byte ExceptionCode { get; }

        public ModbusException(byte exceptionCode, string message) : base(message)
        {
            ExceptionCode = exceptionCode;
        }
    }
}
