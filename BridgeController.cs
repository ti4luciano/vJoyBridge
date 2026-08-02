// ============================================================================
// BridgeController.cs
// ============================================================================
using System;
using vJoyInterfaceWrap;

namespace vJoyBridge
{
    public class BridgeController
    {
        private readonly ISerialService _serialService;
        private readonly IJoystickService _vJoyService;
        private readonly ILogService _log;
        private readonly uint _deviceId;

        public BridgeController(ISerialService serialService, IJoystickService vJoyService, ILogService log, uint deviceId = 1)
        {
            _serialService = serialService;
            _vJoyService = vJoyService;
            _log = log;
            _deviceId = deviceId;

            _serialService.OnMessageReceived += HandleSerialMessage;
            _vJoyService.OnForceFeedbackReceived += HandleForceFeedbackReceived;
        }

        public void Start(string portName, int baudRate)
        {
            if (_vJoyService.Initialize(_deviceId))
            {
                _serialService.Connect(portName, baudRate);
                _log.Info(LogPoint.General, "[Bridge] Ready.");
            }
        }

        public void Stop()
        {
            _serialService.Disconnect();
            _vJoyService.Shutdown(_deviceId);
            _log.Info(LogPoint.General, "[Bridge] Stopped.");
        }

        private void HandleSerialMessage(string message)
        {

            _log.Debug(LogPoint.SerialToVJoy, $"[Serial Received] raw message:{message}");

            string[] tokens = message.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string token in tokens)
            {
                if (token.StartsWith("X:") && int.TryParse(token.Substring(2), out int rawX))
                {
                    _vJoyService.SetAxis(_deviceId, HID_USAGES.HID_USAGE_X, rawX);
                }
                else if (token.StartsWith("Y:") && int.TryParse(token.Substring(2), out int rawY))
                {
                    _vJoyService.SetAxis(_deviceId, HID_USAGES.HID_USAGE_Y, rawY);
                }
                else if (token.StartsWith("Z:") && int.TryParse(token.Substring(2), out int rawZ))
                {
                    _vJoyService.SetAxis(_deviceId, HID_USAGES.HID_USAGE_Z, rawZ);
                }
            }

            // Set 8 initial buttons to false
            for (uint btn = 1; btn <= 8; btn++)
            {
                _vJoyService.SetButton(_deviceId, btn, false);
            }
        }

        private void HandleForceFeedbackReceived(int pwm, int direction)
        {
            string ffbCommand = $"F:{pwm},{direction}\n";
            _serialService.SendMessage(ffbCommand);
        }
    }
}