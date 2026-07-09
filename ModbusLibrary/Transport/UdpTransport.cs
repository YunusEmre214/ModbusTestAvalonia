using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using ModbusLibrary.Utils;

namespace ModbusLibrary.Transport
{
    public class UdpTransport : ITransport
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

            ModbusTrafficLogger.LogTx(request);

            await _udpClient.SendAsync(request, request.Length);

            var receiveTask = _udpClient.ReceiveAsync();
            var delayTask = Task.Delay(_timeout);

            var completedTask = await Task.WhenAny(receiveTask, delayTask);
            if (completedTask == delayTask)
            {
                throw new TimeoutException("UDP Receive timeout. Device is not responding.");
            }

            var result = await receiveTask;

            ModbusTrafficLogger.LogRx(result.Buffer);

            return result.Buffer;
        }
    }
}