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
    // Divide a string onde houver espaço. 
    // Ex: "X:52 Y:761 Z:1023" vira um array ["X:52", "Y:761", "Z:1023"]
    string[] eixos = message.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

    foreach (string eixo in eixos)
    {
        if (eixo.StartsWith("X:"))
        {
            if (int.TryParse(eixo.Substring(2), out int pos))
            {
                 //Console.WriteLine($"X (Encoder): {pos}");
                _vJoyService.SetAxis(_deviceId, HID_USAGES.HID_USAGE_X, pos);
            }
        }
        else if (eixo.StartsWith("Y:"))
        {
            if (int.TryParse(eixo.Substring(2), out int pos))
            {
                //Console.WriteLine($"Y (Desliz A): {vJoyValue}");
                _vJoyService.SetAxis(_deviceId, HID_USAGES.HID_USAGE_Y, pos);
            }
        }
        else if (eixo.StartsWith("Z:"))
        {
            if (int.TryParse(eixo.Substring(2), out int pos))
            {
                //Console.WriteLine($"Z (Desliz B): {vJoyValue}");
                _vJoyService.SetAxis(_deviceId, HID_USAGES.HID_USAGE_Z, pos);
            }
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

// Cole esta função na sua classe C#
        public int Remap(int value, int from1, int to1, int from2, int to2) {
            return (value - from1) * (to2 - from2) / (to1 - from1) + from2;
        }

    }
}