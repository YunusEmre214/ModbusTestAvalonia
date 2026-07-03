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
        private readonly int _timeout = 2000; // 2 seconds timeout

        public async Task ConnectAsync(string ipAddress, int port)
        {
            _udpClient = new UdpClient();
            _endPoint = new IPEndPoint(IPAddress.Parse(ipAddress), port);

            // Set the target endpoint for the UDP connection
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

            // 1. CONVERT MBAP (TCP) FRAME TO RTU FRAME
            // MBAP Frame: [0,1] TransID, [2,3] ProtoID, [4,5] Length, [6] UnitID, [7...] PDU
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

            // 3. SEND RTU FRAME OVER UDP
            await _udpClient.SendAsync(rtuRequest, rtuRequest.Length);

            // 4. READ RTU RESPONSE (With Timeout Mechanism)
            var receiveTask = _udpClient.ReceiveAsync();
            var delayTask = Task.Delay(_timeout);

            var completedTask = await Task.WhenAny(receiveTask, delayTask);
            if (completedTask == delayTask)
            {
                throw new TimeoutException("UDP Receive timeout. The device is not responding.");
            }

            var result = await receiveTask;
            byte[] rtuResponse = result.Buffer;

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

            // Return the reconstructed MBAP frame
            return mbapResponse;
        }
    }
}
