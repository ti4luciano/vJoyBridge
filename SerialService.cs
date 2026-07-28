using System;
using System.IO;
using System.IO.Ports;
using System.Threading;

namespace vJoyBridge
{
    /// <summary>
    /// Implementação robusta de comunicação serial utilizando Thread dedicada (Polling),
    /// com reconexão automática configurável caso a porta caia/seja desconectada.
    /// </summary>
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

            OpenPort(portName, baudRate);

            _isRunning = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();

            _log.Info(LogPoint.General, $"[Serial] Conectado à porta {portName} a {baudRate} bps.");
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
                _log.Warning(LogPoint.General, $"[Serial] Falha ao enviar mensagem (porta pode estar desconectada): {ex.Message}");
            }
        }

        public void Disconnect()
        {
            _isRunning = false;
            _readThread?.Join(500); // Aguarda até 500ms para a thread fechar

            ClosePortSafely();
            _log.Info(LogPoint.General, "[Serial] Porta fechada.");
        }

        private void OpenPort(string portName, int baudRate)
        {
            _serialPort = new SerialPort(portName, baudRate)
            {
                DtrEnable = true, // Essencial para Arduino/STM32 via USB
                ReadTimeout = 50,
                NewLine = "\n"
            };

            _serialPort.Open();
            _serialPort.DiscardInBuffer(); // Limpa lixo residual do buffer
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
            catch
            {
                // Ignorado: a porta pode já estar em estado inválido (ex: dispositivo removido).
            }
        }

        /// <summary>
        /// Loop de alta performance para leitura contínua da porta serial.
        /// Em caso de desconexão inesperada, aciona a rotina de reconexão (se habilitada).
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
                catch (TimeoutException)
                {
                    /* Timeout esperado do ReadLine */
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // Casos típicos de porta caída: cabo desconectado, dispositivo removido,
                    // porta fechada externamente, etc.
                    if (!_isRunning) break;

                    _log.Warning(LogPoint.General, $"[Serial] Conexão perdida com {_portName}: {ex.Message}");
                    ClosePortSafely();

                    if (!HandleReconnection())
                    {
                        // Reconexão desabilitada ou tentativas esgotadas: encerra a thread de leitura.
                        _isRunning = false;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(LogPoint.General, $"[Serial] Erro na leitura: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }

        /// <summary>
        /// Tenta reabrir a porta serial de acordo com o config.json (Serial.Reconnect).
        /// Retorna true se reconectou com sucesso, false se desistiu.
        /// </summary>
        private bool HandleReconnection()
        {
            if (!_reconnectConfig.Enabled)
            {
                _log.Error(LogPoint.General, "[Serial] Reconexão automática desabilitada no config.json. Encerrando leitura.");
                return false;
            }

            int maxAttempts = _reconnectConfig.MaxAttempts; // 0 = tentativas ilimitadas
            int attempt = 0;

            while (_isRunning && (maxAttempts <= 0 || attempt < maxAttempts))
            {
                attempt++;
                string limiteStr = maxAttempts > 0 ? $"/{maxAttempts}" : " (ilimitado)";
                _log.Info(LogPoint.General, $"[Serial] Tentativa de reconexão {attempt}{limiteStr} em {_reconnectConfig.DelayMs}ms...");

                Thread.Sleep(_reconnectConfig.DelayMs);

                if (!_isRunning) return false; // Disconnect() foi chamado enquanto esperávamos

                try
                {
                    OpenPort(_portName, _baudRate);
                    _log.Info(LogPoint.General, $"[Serial] Reconectado à porta {_portName} com sucesso.");
                    return true;
                }
                catch (Exception ex)
                {
                    _log.Warning(LogPoint.General, $"[Serial] Falha na tentativa {attempt}: {ex.Message}");
                }
            }

            _log.Error(LogPoint.General, "[Serial] Número máximo de tentativas de reconexão atingido. Desistindo.");
            return false;
        }
    }
}
