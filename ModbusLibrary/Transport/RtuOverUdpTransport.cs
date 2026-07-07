using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using ModbusLibrary.Utils;

namespace ModbusLibrary.Transport
{
    public class RtuOverUdpTransport : ITransport
    {
        private UdpClient _udpClient;
        private IPEndPoint _endPoint;
        private readonly int _timeout = 2000;

        public async Task ConnectAsync(string address, int port)
        {
            _udpClient = new UdpClient();
            _endPoint = new IPEndPoint(IPAddress.Parse(address), port);
            _udpClient.Connect(_endPoint);

            await Task.CompletedTask;
        }

        public void Disconnect()
        {
            if (_udpClient != null)
            {
                _udpClient.Close();
                _udpClient.Dispose();
                _udpClient = null;
            }
        }

        public async Task<byte[]> SendAndReceiveAsync(byte[] request)
        {
            if (_udpClient == null)
                throw new InvalidOperationException("UDP Client is not connected.");

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

            await _udpClient.SendAsync(rtuRequest, rtuRequest.Length);

            var receiveTask = _udpClient.ReceiveAsync();
            var delayTask = Task.Delay(_timeout);

            var completedTask = await Task.WhenAny(receiveTask, delayTask);
            if (completedTask == delayTask)
            {
                throw new TimeoutException("UDP Receive timeout. The device is not responding.");
            }

            var result = await receiveTask;
            byte[] rtuResponse = result.Buffer;

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