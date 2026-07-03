using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading; 
using System.Threading.Tasks;

namespace ModbusLibrary.Transport
{
    public class TcpTransport:ITransport
    {
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public async Task ConnectAsync(string ipAddress, int port = 502)
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
            if (_stream == null || !_stream.CanRead || !_stream.CanWrite)
                throw new InvalidOperationException("Network connection is not active!");

            
            await _lock.WaitAsync();
            try
            {
                await _stream.WriteAsync(request, 0, request.Length);

                byte[] mbapHeader = new byte[6];
                await ReadExactBytesAsync(_stream, mbapHeader, 6);

                ushort remainingBytesToRead = BinaryPrimitives.ReadUInt16BigEndian(mbapHeader.AsSpan(4, 2));

                byte[] payload = new byte[remainingBytesToRead];
                await ReadExactBytesAsync(_stream, payload, remainingBytesToRead);

                byte[] fullResponse = new byte[6 + remainingBytesToRead];
                Array.Copy(mbapHeader, 0, fullResponse, 0, 6);
                Array.Copy(payload, 0, fullResponse, 6, remainingBytesToRead);

                return fullResponse;
            }
            finally
            {
                
                _lock.Release();
            }
        }

        private async Task ReadExactBytesAsync(NetworkStream stream, byte[] buffer, int bytesToRead)
        {
            int totalRead = 0;
            while (totalRead < bytesToRead)
            {
                int read = await stream.ReadAsync(buffer, totalRead, bytesToRead - totalRead);
                if (read == 0)
                    throw new System.IO.IOException("The device disconnected.");
                totalRead += read;
            }
        }
    }
}
