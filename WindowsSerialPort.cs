namespace rt4k_pi;

internal class WindowsSerialPort
{
    internal static string? FindPort()
    {
        var ports = System.IO.Ports.SerialPort.GetPortNames();
        if (ports.Length > 0)
        {
            Console.WriteLine($"Found COM port via GetPortNames(): {ports[0]}");
            return ports[0];
        }

        return null;
    }

    internal static Stream OpenAndConfigure(string portName, int baudRate)
    {
        Console.WriteLine($"Opening {portName} at {baudRate} baud via SerialPort...");
        var sp = new System.IO.Ports.SerialPort(portName, baudRate)
        {
            DataBits = 8,
            Parity = System.IO.Ports.Parity.None,
            StopBits = System.IO.Ports.StopBits.One,
            // Streaming uploads depend on the device's CTS# backpressure, so the host must
            // actually honour it. Without RTS/CTS we overrun the device's 2048 byte RX ring.
            Handshake = System.IO.Ports.Handshake.RequestToSend,
            ReadTimeout = -1,
            WriteTimeout = -1,
            // Streaming downloads have no flow control at all, so the driver buffer is the only
            // thing absorbing a scheduling hiccup on our side. Make it generous.
            ReadBufferSize = 1 << 20,
            WriteBufferSize = 1 << 16
        };

        sp.Open();
        Console.WriteLine($"Port opened and configured: {baudRate} baud, 8 data bits, no parity, 1 stop bit, RTS/CTS");

        return new SerialPortStreamAdapter(sp);
    }

    private class SerialPortStreamAdapter(System.IO.Ports.SerialPort sp) : Stream
    {
        private readonly System.IO.Ports.SerialPort serialPort = sp;

        public override bool CanRead => serialPort.IsOpen;
        public override bool CanWrite => serialPort.IsOpen;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            // SerialPort doesn't expose Flush, but data is already written
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return serialPort.Read(buffer, offset, count);
            }
            catch (TimeoutException)
            {
                return 0;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            serialPort.Write(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && serialPort != null)
            {
                if (serialPort.IsOpen)
                {
                    serialPort.Close();
                }
                serialPort.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}