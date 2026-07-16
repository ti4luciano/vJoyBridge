using System;
using System.Timers;
using vJoyInterfaceWrap;

namespace vJoyBridge
{
    /// <summary>
    /// Coordena a comunicação entre a Serial e o vJoy.
    /// Contém a regra de conversão de dados e lógica de Force Feedback.
    /// </summary>
    public class BridgeController
    {
        private readonly ISerialService _serialService;
        private readonly IJoystickService _vJoyService;
        private readonly uint _deviceId;
        
        // Timer para enviar dados de Força para o STM32 (FFB Simulado)
        private readonly Timer _ffbTimer;
        private bool _motorDirection = false;

        public BridgeController(ISerialService serialService, IJoystickService vJoyService, uint deviceId = 1)
        {
            _serialService = serialService;
            _vJoyService = vJoyService;
            _deviceId = deviceId;

            // Inscreve-se no evento de mensagem recebida
            _serialService.OnMessageReceived += HandleSerialMessage;

            // Configura o timer para enviar feedback ao STM32 a cada 500ms
            _ffbTimer = new Timer(500);
            _ffbTimer.Elapsed += SendForceFeedback;
        }

        public void Start(string portName, int baudRate)
        {
            if (_vJoyService.Initialize(_deviceId))
            {
                _serialService.Connect(portName, baudRate);
                _ffbTimer.Start();
                Console.WriteLine("[Bridge] Sistema operante. Tradução iniciada.");
            }
        }

        public void Stop()
        {
            _ffbTimer.Stop();
            _serialService.Disconnect();
            _vJoyService.Shutdown(_deviceId);
            Console.WriteLine("[Bridge] Sistema encerrado.");
        }

        /// <summary>
        /// Processa a mensagem vinda do STM32 e aplica no vJoy.
        /// </summary>
        private void HandleSerialMessage(string message)
        {
            if (message.StartsWith("X:"))
            {
                string valStr = message.Substring(2);
                if (int.TryParse(valStr, out int pos))
                {
                    // Conversão de int16 (-32768 a 32767) para vJoy uint16 (0 a 32767)
                    int vJoyValue = (pos + 32768) / 2;
                    _vJoyService.SetAxis(_deviceId, HID_USAGES.HID_USAGE_X, vJoyValue);
                    
                    // Descomente para debugar no console:
                    // Console.WriteLine($"[STM32] Pos: {pos} -> [vJoy] {vJoyValue}");
                }
            }
        }

        /// <summary>
        /// Envia comandos de força motriz (PWM e Direção) de volta ao microcontrolador.
        /// Formato esperado pelo seu STM32: "F:{pwm},{direcao}"
        /// </summary>
        private void SendForceFeedback(object? sender, ElapsedEventArgs e)
        {
            // Alterna a direção e gera uma força simulada para testes
            int pwm = 150; // Força simulada
            int dir = _motorDirection ? 1 : 0;
            _motorDirection = !_motorDirection; // Inverte para o próximo ciclo

            string ffbCommand = $"F:{pwm},{dir}\n";
            _serialService.SendMessage(ffbCommand);

            // Log para você visualizar que o dado está sendo enviado
            Console.WriteLine($"[FFB] Enviado para STM32: {ffbCommand.Trim()}");
        }
    }
}