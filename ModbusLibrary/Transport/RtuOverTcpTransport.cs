using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using ModbusLibrary.Utils;

namespace ModbusLibrary.Transport
{
    public class RtuOverTcpTransport : ITransport
    {
        private TcpClient _tcpClient;
        private NetworkStream _stream;

        public async Task ConnectAsync(string ipAddress, int port)
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(ipAddress, port);
            _stream = _tcpClient.GetStream();
        }

        public void Disconnect()
        {
            _stream?.Close();
            _tcpClient?.Close();
        }

        public async Task<byte[]> SendAndReceiveAsync(byte[] request)
        {
            if (_stream == null || !_tcpClient.Connected)
                throw new InvalidOperationException("Not connected to the server.");

            // 1. CONVERT MBAP (TCP) FRAME TO RTU FRAME
            // MBAP Frame: [0,1] TransID, [2,3] ProtoID, [4,5] Length, [6] UnitID, [7...] PDU (Function Code + Data)
            int pduLength = request.Length - 7;
            byte[] rtuRequest = new byte[1 + pduLength + 2]; // SlaveID(1) + PDU + CRC(2)

            rtuRequest[0] = request[6]; // Extract Unit ID (Slave ID)
            Array.Copy(request, 7, rtuRequest, 1, pduLength); // Extract PDU

            // 2. CALCULATE AND APPEND CRC16
            byte[] payloadToCrc = new byte[rtuRequest.Length - 2];
            Array.Copy(rtuRequest, 0, payloadToCrc, 0, payloadToCrc.Length);

            byte[] crcBytes = Crc16.Calculate(payloadToCrc);
            rtuRequest[rtuRequest.Length - 2] = crcBytes[0]; // CRC Low Byte
            rtuRequest[rtuRequest.Length - 1] = crcBytes[1]; // CRC High Byte

            // 3. SEND RTU FRAME OVER TCP
            await _stream.WriteAsync(rtuRequest, 0, rtuRequest.Length);

            // 4. READ RTU RESPONSE
            byte[] buffer = new byte[256];
            int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);

            if (bytesRead == 0)
                throw new Exception("Connection closed by remote host.");

            byte[] rtuResponse = new byte[bytesRead];
            Array.Copy(buffer, 0, rtuResponse, 0, bytesRead);

            // 5. VALIDATE CRC OF THE RESPONSE
            if (!Crc16.IsValid(rtuResponse))
            {
                throw new Exception("CRC validation failed! The data might be corrupted.");
            }

            // 6. CONVERT RTU RESPONSE BACK TO MBAP (TCP) FRAME
            // RTU Response: [0] SlaveID, [1...] PDU, [N-2, N-1] CRC
            int responsePduLength = rtuResponse.Length - 3; // Total Length - SlaveID(1) - CRC(2)
            byte[] mbapResponse = new byte[7 + responsePduLength];

            // Copy Transaction ID and Protocol ID from the original request
            mbapResponse[0] = request[0];
            mbapResponse[1] = request[1];
            mbapResponse[2] = request[2];
            mbapResponse[3] = request[3];

            // Calculate new MBAP length (Unit ID + PDU)
            int mbapLength = 1 + responsePduLength;
            mbapResponse[4] = (byte)(mbapLength >> 8);
            mbapResponse[5] = (byte)(mbapLength & 0xFF);

            // Set Unit ID and copy PDU
            mbapResponse[6] = rtuResponse[0];
            Array.Copy(rtuResponse, 1, mbapResponse, 7, responsePduLength);

            // Return the fake MBAP frame so ModbusMaster can decode it normally
            return mbapResponse;
        }
    }
}
