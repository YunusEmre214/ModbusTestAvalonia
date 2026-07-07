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

        public async Task ConnectAsync(string address, int port)
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(address, port);
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

            int pduLength = request.Length - 7;
            byte[] rtuRequest = new byte[1 + pduLength + 2];

            rtuRequest[0] = request[6];
            Array.Copy(request, 7, rtuRequest, 1, pduLength);

            byte[] payloadToCrc = new byte[rtuRequest.Length - 2];
            Array.Copy(rtuRequest, 0, payloadToCrc, 0, payloadToCrc.Length);

            byte[] crcBytes = Crc16.Calculate(payloadToCrc);
            rtuRequest[rtuRequest.Length - 2] = crcBytes[0];
            rtuRequest[rtuRequest.Length - 1] = crcBytes[1];

            ModbusTrafficLogger.LogTx(rtuRequest);

            await _stream.WriteAsync(rtuRequest, 0, rtuRequest.Length);

            byte[] buffer = new byte[256];
            int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);

            if (bytesRead == 0)
                throw new Exception("Connection closed by remote host.");

            byte[] rtuResponse = new byte[bytesRead];
            Array.Copy(buffer, 0, rtuResponse, 0, bytesRead);

            ModbusTrafficLogger.LogRx(rtuResponse);

            if (!Crc16.IsValid(rtuResponse))
            {
                throw new Exception("CRC validation failed! The data might be corrupted.");
            }

            int responsePduLength = rtuResponse.Length - 3;
            byte[] mbapResponse = new byte[7 + responsePduLength];

            mbapResponse[0] = request[0];
            mbapResponse[1] = request[1];
            mbapResponse[2] = request[2];
            mbapResponse[3] = request[3];

            int mbapLength = 1 + responsePduLength;
            mbapResponse[4] = (byte)(mbapLength >> 8);
            mbapResponse[5] = (byte)(mbapLength & 0xFF);
            mbapResponse[6] = rtuResponse[0];
            Array.Copy(rtuResponse, 1, mbapResponse, 7, responsePduLength);

            return mbapResponse;
        }
    }
}