using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace ModbusLibrary.Transport
{
    public interface ITransport
    {
        Task ConnectAsync(string ipAddress, int port);
        void Disconnect();
        Task<byte[]> SendAndReceiveAsync(byte[] request);

    }
}
