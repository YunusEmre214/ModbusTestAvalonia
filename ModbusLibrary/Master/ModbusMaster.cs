using System;
using System.Threading.Tasks;
using ModbusLibrary.Protocol;
using ModbusLibrary.Transport;

namespace ModbusLibrary.Master
{
    public class ModbusMaster
    {
        private readonly ITransport _transport;
        private ushort _transactionId;

        public ModbusMaster(ITransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _transactionId = 0;
        }

        // 01 Read Coils
        public async Task<bool[]> ReadCoilsAsync(byte unitId, ushort startAddress, ushort quantity)
        {
            byte[] req = ModbusFrameEncoder.EncodeReadRequest(++_transactionId, unitId, (byte)FunctionCode.ReadCoils, startAddress, quantity);
            byte[] res = await _transport.SendAndReceiveAsync(req);
            return ModbusFrameDecoder.DecodeReadBitsResponse(res, (byte)FunctionCode.ReadCoils, quantity);
        }

        // 02 Read Discrete Inputs
        public async Task<bool[]> ReadDiscreteInputsAsync(byte unitId, ushort startAddress, ushort quantity)
        {
            byte[] req = ModbusFrameEncoder.EncodeReadRequest(++_transactionId, unitId, (byte)FunctionCode.ReadDiscreteInputs, startAddress, quantity);
            byte[] res = await _transport.SendAndReceiveAsync(req);
            return ModbusFrameDecoder.DecodeReadBitsResponse(res, (byte)FunctionCode.ReadDiscreteInputs, quantity);
        }

        // 03 Read Holding Registers
        public async Task<ushort[]> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort quantity)
        {
            byte[] req = ModbusFrameEncoder.EncodeReadRequest(++_transactionId, unitId, (byte)FunctionCode.ReadHoldingRegisters, startAddress, quantity);
            byte[] res = await _transport.SendAndReceiveAsync(req);
            return ModbusFrameDecoder.DecodeReadRegistersResponse(res, (byte)FunctionCode.ReadHoldingRegisters);
        }

        // 04 Read Input Registers
        public async Task<ushort[]> ReadInputRegistersAsync(byte unitId, ushort startAddress, ushort quantity)
        {
            byte[] req = ModbusFrameEncoder.EncodeReadRequest(++_transactionId, unitId, (byte)FunctionCode.ReadInputRegisters, startAddress, quantity);
            byte[] res = await _transport.SendAndReceiveAsync(req);
            return ModbusFrameDecoder.DecodeReadRegistersResponse(res, (byte)FunctionCode.ReadInputRegisters);
        }

        // 05 Write Single Coil
        public async Task WriteSingleCoilAsync(byte unitId, ushort coilAddress, bool value)
        {
            byte[] req = ModbusFrameEncoder.EncodeWriteSingleCoilRequest(++_transactionId, unitId, coilAddress, value);
            byte[] res = await _transport.SendAndReceiveAsync(req);
            ModbusFrameDecoder.DecodeWriteResponse(res, (byte)FunctionCode.WriteSingleCoil, coilAddress);
        }

        // 06 Write Single Register
        public async Task WriteSingleRegisterAsync(byte unitId, ushort registerAddress, ushort value)
        {
            byte[] req = ModbusFrameEncoder.EncodeWriteSingleRegisterRequest(++_transactionId, unitId, registerAddress, value);
            byte[] res = await _transport.SendAndReceiveAsync(req);
            ModbusFrameDecoder.DecodeWriteResponse(res, (byte)FunctionCode.WriteSingleRegister, registerAddress);
        }
        public async Task WriteMultipleRegistersAsync(byte unitId, ushort startAddress, ushort[] values)
        {
            ushort currentTransactionId = ++_transactionId;
            byte[] requestFrame = ModbusFrameEncoder.EncodeWriteMultipleRegistersRequest(currentTransactionId, unitId, startAddress, values);
            byte[] responseFrame = await _transport.SendAndReceiveAsync(requestFrame);
            ModbusFrameDecoder.DecodeWriteMultipleRegistersResponse(responseFrame, startAddress);
        }
    }
}
