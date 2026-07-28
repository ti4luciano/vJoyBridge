// ============================================================================
// SerialService.cs
// ============================================================================
using System;
using System.IO;
using System.IO.Ports;
using System.Threading;

namespace vJoyBridge
{
    public class SerialService : ISerialService
    {
        public event Action<string> OnMessageReceived;

        private readonly ILogService _log;
        private readonly ReconnectConfig _reconnectConfig;
        private SerialPort _serialPort;
        private Thread _readThread;
        private volatile bool _isRunning;
        private string _portName;
        private int _baudRate;

        public SerialService(ILogService log, ReconnectConfig reconnectConfig)
        {
            _log = log;
            _reconnectConfig = reconnectConfig;
        }

        public void Connect(string portName, int baudRate)
        {
            _portName = portName;
            _baudRate = baudRate;

            // Define como true logo no início, pois o HandleReconnection precisa
            // dessa flag ativa para iterar pelas tentativas caso a conexão inicial falhe.
            _isRunning = true; 

            try
            {
                OpenPort(portName, baudRate);
                _log.Info(LogPoint.General, $"[Serial] Connected to {portName} @ {baudRate} bps.");
                
                _readThread = new Thread(ReadLoop) { IsBackground = true };
                _readThread.Start();
            }
            catch (Exception ex)
            {
                _log.Warning(LogPoint.General, $"[Serial] Initial connection failed: {ex.Message}");
                
                // Direciona para as reconexões caso a primeira falhe
                if (HandleReconnection())
                {
                    _readThread = new Thread(ReadLoop) { IsBackground = true };
                    _readThread.Start();
                }
                else
                {
                    // Encerra o programa após falhar todas as tentativas
                    _isRunning = false;
                    _log.Error(LogPoint.General, "[Serial] Reconnection attempts exhausted. Terminating program.");
                    Environment.Exit(1);
                }
            }
        }

        public void SendMessage(string message)
        {
            try
            {
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    _serialPort.Write(message);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(LogPoint.General, $"[Serial] Tx failure: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            _isRunning = false;
            _readThread?.Join(500);
            ClosePortSafely();
            _log.Info(LogPoint.General, "[Serial] Closed.");
        }

        private void OpenPort(string portName, int baudRate)
        {
            _serialPort = new SerialPort(portName, baudRate)
            {
                DtrEnable = true,
                ReadTimeout = 50,
                NewLine = "\n"
            };

            _serialPort.Open();
            _serialPort.DiscardInBuffer();

            // Handshake: envia o caractere 'H' assim que a conexão for estabelecida
            _serialPort.Write("H");
        }

        private void ClosePortSafely()
        {
            try
            {
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    _serialPort.Close();
                }
            }
            catch { }
        }

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

                    string line = _serialPort.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        OnMessageReceived?.Invoke(line.Trim());
                    }
                }
                catch (TimeoutException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    if (!_isRunning) break;
                    _log.Warning(LogPoint.General, $"[Serial] Connection lost on {_portName}: {ex.Message}");
                    ClosePortSafely();

                    if (!HandleReconnection())
                    {
                        _isRunning = false;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(LogPoint.General, $"[Serial] Read error: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }

        private bool HandleReconnection()
        {
            if (!_reconnectConfig.Enabled) return false;

            int maxAttempts = _reconnectConfig.MaxAttempts;
            int attempt = 0;

            while (_isRunning && (maxAttempts <= 0 || attempt < maxAttempts))
            {
                attempt++;
                Thread.Sleep(_reconnectConfig.DelayMs);

                if (!_isRunning) return false;

                try
                {
                    OpenPort(_portName, _baudRate);
                    _log.Info(LogPoint.General, $"[Serial] Reconnected to {_portName}.");
                    return true;
                }
                catch (Exception ex)
                {
                    _log.Warning(LogPoint.General, $"[Serial] Reconnect attempt {attempt} failed: {ex.Message}");
                }
            }

            _log.Error(LogPoint.General, "[Serial] Reconnection attempts exhausted.");
            return false;
        }
    }
}