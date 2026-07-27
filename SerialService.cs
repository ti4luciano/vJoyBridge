using System;
using System.IO.Ports;
using System.Threading;

namespace vJoyBridge
{
    /// <summary>
    /// Implementação robusta de comunicação serial utilizando Thread dedicada (Polling).
    /// </summary>
    public class SerialService : ISerialService
    {
        public event Action<string> OnMessageReceived;

        private readonly ILogService _log;
        private SerialPort _serialPort;
        private Thread _readThread;
        private bool _isRunning;

        public SerialService(ILogService log)
        {
            _log = log;
        }

        public void Connect(string portName, int baudRate)
        {
            _serialPort = new SerialPort(portName, baudRate)
            {
                DtrEnable = true, // Essencial para Arduino/STM32 via USB
                ReadTimeout = 50,
                NewLine = "\n"
            };

            _serialPort.Open();
            _serialPort.DiscardInBuffer(); // Limpa lixo residual do buffer

            _isRunning = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();

            _log.Info(LogPoint.General, $"[Serial] Conectado à porta {portName} a {baudRate} bps.");
        }

        public void SendMessage(string message)
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Write(message);
            }
        }

        public void Disconnect()
        {
            _isRunning = false;
            _readThread?.Join(500); // Aguarda até 500ms para a thread fechar

            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
                _log.Info(LogPoint.General, "[Serial] Porta fechada.");
            }
        }

        /// <summary>
        /// Loop de alta performance para leitura contínua da porta serial.
        /// </summary>
        private void ReadLoop()
        {
            if (_serialPort == null) return;

            while (_isRunning)
            {
                try
                {
                    if (_serialPort.BytesToRead == 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    string linha = _serialPort.ReadLine();
                    if (!string.IsNullOrWhiteSpace(linha))
                    {
                        // Dispara o evento notificando o controlador
                        OnMessageReceived?.Invoke(linha.Trim());
                    }
                }
                catch (TimeoutException) { /* Timeout esperado do ReadLine */ }
                catch (Exception ex)
                {
                    _log.Error(LogPoint.General, $"[Serial] Erro na leitura: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }
    }
}
