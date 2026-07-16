using System;
using vJoyInterfaceWrap;

namespace vJoyBridge
{
    public class BridgeController
    {
        private readonly ISerialService _serialService;
        private readonly IJoystickService _vJoyService;
        private readonly uint _deviceId;

        public BridgeController(ISerialService serialService, IJoystickService vJoyService, uint deviceId = 1)
        {
            _serialService = serialService;
            _vJoyService = vJoyService;
            _deviceId = deviceId;

            // Inscrição: Posição do STM32 -> vJoy
            _serialService.OnMessageReceived += HandleSerialMessage;

            // Inscrição: Força do vJoy (Jogo) -> STM32
            _vJoyService.OnForceFeedbackReceived += HandleForceFeedbackReceived;
        }

        public void Start(string portName, int baudRate)
        {
            if (_vJoyService.Initialize(_deviceId))
            {
                _serialService.Connect(portName, baudRate);
                Console.WriteLine("[Bridge] Pronto para traduzir posição e FFB.");
            }
        }

        public void Stop()
        {
            _serialService.Disconnect();
            _vJoyService.Shutdown(_deviceId);
            Console.WriteLine("[Bridge] Encerrado.");
        }

        /// <summary>
        /// Processa a rotação lida no STM32 e atualiza o eixo do vJoy.
        /// </summary>
        private void HandleSerialMessage(string message)
        {
            if (message.StartsWith("X:"))
            {
                string valStr = message.Substring(2);
                if (int.TryParse(valStr, out int pos))
                {
                    int vJoyValue = (pos + 32768) / 2;
                    _vJoyService.SetAxis(_deviceId, HID_USAGES.HID_USAGE_X, vJoyValue);
                }
            }
        }

        /// <summary>
        /// Captura o FFB real gerado pelo vJoy e envia via Serial para o STM32.
        /// </summary>
        private void HandleForceFeedbackReceived(int pwm, int direction)
        {
            // Monta a mensagem no padrão "F:{pwm},{dir}\n"
            string ffbCommand = $"F:{pwm},{direction}\n";
            
            _serialService.SendMessage(ffbCommand);
            
            Console.WriteLine($"[Bridge -> STM32] FFB Enviado: {ffbCommand.Trim()}");
        }
    }
}