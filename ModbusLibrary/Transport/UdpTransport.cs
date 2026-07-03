using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace ModbusLibrary.Transport
{
    public class UdpTransport : ITransport
    {
        private UdpClient _udpClient;
        private IPEndPoint _endPoint;
        private readonly int _timeout = 2000; // 2 second timeout

        public async Task ConnectAsync(string ipAddress, int port)
        {
            _udpClient = new UdpClient();
            _endPoint = new IPEndPoint(IPAddress.Parse(ipAddress), port);

            // UDP is a connectionless protocol, "Connect" only fixes the destination address.
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

            // 1. Submit Request
            await _udpClient.SendAsync(request, request.Length);

            // 2. Wait for the answer (using the timeout mechanism)
            var receiveTask = _udpClient.ReceiveAsync();
            var delayTask = Task.Delay(_timeout);

            // Throw an error if the timeout expires before a response is received
            var completedTask = await Task.WhenAny(receiveTask, delayTask);
            if (completedTask == delayTask)
            {
                throw new TimeoutException("UDP Receive timeout. Device is not responding.");
            }

            var result = await receiveTask;
            return result.Buffer;
        }
    }
}