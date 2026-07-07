using System;
using System.IO.Ports;
using System.Threading.Tasks;
using ModbusLibrary.Utils;

namespace ModbusLibrary.Transport
{
    public class SerialTransport : ITransport
    {
        private SerialPort _serialPort;
        private readonly int _timeout = 2000;

        // Seri porta özel değişkenler
        private readonly int _dataBits;
        private readonly Parity _parity;
        private readonly StopBits _stopBits;

        // CONSTRUCTOR: Sınıf yaratılırken ayarları buraya alıyoruz (Varsayılan 8-N-1)
        public SerialTransport(int dataBits = 8, Parity parity = Parity.None, StopBits stopBits = StopBits.One)
        {
            _dataBits = dataBits;
            _parity = parity;
            _stopBits = stopBits;
        }

        public async Task ConnectAsync(string portName, int baudRate)
        {
            // Sabit değerler yerine, yukarıdaki değişkenleri kullanıyoruz
            _serialPort = new SerialPort(portName, baudRate, _parity, _dataBits, _stopBits)
            {
                ReadTimeout = _timeout,
                WriteTimeout = _timeout
            };

            _serialPort.Open();

            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();

        }

        public void Disconnect()
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
                _serialPort.Dispose();
                _serialPort = null;
            }
        }

        public async Task<byte[]> SendAndReceiveAsync(byte[] request)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
                throw new InvalidOperationException("Serial port is not open.");

            // 1. CONVERT MBAP (TCP) FRAME TO RTU FRAME
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

            // Clear buffers before sending new request
            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();

            // 3. SEND RTU FRAME OVER SERIAL PORT
            _serialPort.Write(rtuRequest, 0, rtuRequest.Length);

            // 4. READ RTU RESPONSE
            // Wait slightly for the hardware device to process and reply
            await Task.Delay(50);

            int bytesToRead = _serialPort.BytesToRead;
            int retryCount = 0;

            // Polling mechanism to wait for full frame arrival (max ~2 seconds)
            while (bytesToRead < 4 && retryCount < 40)
            {
                await Task.Delay(50);
                bytesToRead = _serialPort.BytesToRead;
                retryCount++;
            }

            if (bytesToRead == 0)
                throw new TimeoutException("Serial port receive timeout. The device is not responding.");

            byte[] rtuResponse = new byte[bytesToRead];
            _serialPort.Read(rtuResponse, 0, bytesToRead);

            // 5. VALIDATE CRC OF THE RESPONSE
            if (!Crc16.IsValid(rtuResponse))
            {
                throw new Exception("CRC validation failed! Serial line noise detected.");
            }

            // 6. CONVERT RTU RESPONSE BACK TO MBAP (TCP) FRAME
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
