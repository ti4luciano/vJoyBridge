using System;
using vJoyInterfaceWrap;

namespace vJoyBridge
{
    public class BridgeController
    {
        private readonly ISerialService _serialService;
        private readonly IJoystickService _vJoyService;
        private readonly ILogService _log;
        private readonly AxisRangeConfig _axisXConfig;
        private readonly uint _deviceId;

        public BridgeController(ISerialService serialService, IJoystickService vJoyService, ILogService log, AxisRangeConfig axisXConfig, uint deviceId = 1)
        {
            _serialService = serialService;
            _vJoyService = vJoyService;
            _log = log;
            _axisXConfig = axisXConfig;
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
                _log.Info(LogPoint.General, "[Bridge] Pronto para traduzir posição e FFB.");
            }
        }

        public void Stop()
        {
            _serialService.Disconnect();
            _vJoyService.Shutdown(_deviceId);
            _log.Info(LogPoint.General, "[Bridge] Encerrado.");
        }

        /// <summary>
        /// Processa a rotação lida no STM32 e atualiza o eixo do vJoy.
        /// Ponto de log: SerialToVJoy.
        /// </summary>
        private void HandleSerialMessage(string message)
        {
            // Divide a string onde houver espaço.
            // Ex: "X:52 Y:761 Z:1023" vira um array ["X:52", "Y:761", "Z:1023"]
            string[] eixos = message.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            _log.Debug(LogPoint.SerialToVJoy, $"[Serial -> vJoy] Mensagem recebida: \"{message}\"");

            foreach (string eixo in eixos)
            {
                if (eixo.StartsWith("X:"))
                {
                    if (int.TryParse(eixo.Substring(2), out int rawPos))
                    {
                        // O encoder do volante hoje produz valores em [AxisX.RawMin, AxisX.RawMax]
                        // (configurável em config.json). Remapeamos para a faixa lógica do vJoy
                        // (0..32767) antes de enviar — isso também é o que dá precisão correta
                        // para o cálculo de Spring/Damper/Inertia no VJoyService.
                        int clamped = Math.Clamp(rawPos, _axisXConfig.RawMin, _axisXConfig.RawMax);
                        int remapped = Remap(clamped, _axisXConfig.RawMin, _axisXConfig.RawMax, 0, 32767);

                        _log.Debug(LogPoint.SerialToVJoy,
                            $"[Serial -> vJoy] X (Encoder): bruto={rawPos} (faixa {_axisXConfig.RawMin}-{_axisXConfig.RawMax}) -> vJoy={remapped}");

                        _vJoyService.SetAxis(_deviceId, HID_USAGES.HID_USAGE_X, remapped);
                    }
                }
                else if (eixo.StartsWith("Y:"))
                {
                    if (int.TryParse(eixo.Substring(2), out int pos))
                    {
                        _log.Debug(LogPoint.SerialToVJoy, $"[Serial -> vJoy] Y (Desliz A): {pos}");
                        _vJoyService.SetAxis(_deviceId, HID_USAGES.HID_USAGE_Y, pos);
                    }
                }
                else if (eixo.StartsWith("Z:"))
                {
                    if (int.TryParse(eixo.Substring(2), out int pos))
                    {
                        _log.Debug(LogPoint.SerialToVJoy, $"[Serial -> vJoy] Z (Desliz B): {pos}");
                        _vJoyService.SetAxis(_deviceId, HID_USAGES.HID_USAGE_Z, pos);
                    }
                }
                else
                {
                    _log.Warning(LogPoint.SerialToVJoy, $"[Serial -> vJoy] Token não reconhecido: \"{eixo}\"");
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

            _log.Debug(LogPoint.VJoyEvents, $"[Bridge -> STM32] FFB Enviado: {ffbCommand.Trim()}");
        }

        /// <summary>
        /// Remapeia linearmente um valor de uma faixa [from1, to1] para [from2, to2].
        /// </summary>
        public int Remap(int value, int from1, int to1, int from2, int to2)
        {
            if (to1 == from1) return from2; // evita divisão por zero se a faixa bruta estiver mal configurada
            return (int)Math.Round((double)(value - from1) * (to2 - from2) / (to1 - from1) + from2);
        }

    }
}
