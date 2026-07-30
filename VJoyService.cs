// ============================================================================
// VJoyService.cs
// ============================================================================
#nullable enable
using System;
using vJoyInterfaceWrap;

namespace vJoyBridge
{
    /// <summary>
    /// Adaptador fino para o driver vJoy: aquisição/liberação do dispositivo virtual,
    /// escrita de eixos/botões e registro do callback nativo de FFB.
    ///
    /// Propositalmente NÃO contém nenhuma lógica de parsing/estado/cálculo de Force
    /// Feedback - isso é responsabilidade exclusiva de <see cref="ForceFeedbackHandler"/>.
    /// Esta classe apenas repassa o ponteiro do pacote nativo (via callback do vJoy) e as
    /// amostras de eixo X para o handler, e encaminha o resultado (PWM/direção) adiante.
    /// </summary>
    public class VJoyService : IJoystickService
    {
        public event Action<int, int>? OnForceFeedbackReceived
        {
            add => _ffbHandler.OnForceFeedbackReceived += value;
            remove => _ffbHandler.OnForceFeedbackReceived -= value;
        }

        private readonly ILogService _log;
        private readonly ForceFeedbackHandler _ffbHandler;
        private vJoyInterfaceWrap.vJoy? _joystick;

        public VJoyService(ILogService log, ForceFeedbackConfig ffbConfig, AxisRangeConfig axisConfig)
        {
            _log = log;
            _ffbHandler = new ForceFeedbackHandler(log, ffbConfig, axisConfig);
        }

        public bool Initialize(uint deviceId)
        {
            _joystick = new vJoyInterfaceWrap.vJoy();

            if (!_joystick.vJoyEnabled())
            {
                _log.Error(LogPoint.General, "[vJoy] Driver or DLL missing.");
                return false;
            }

            VjdStat status = _joystick.GetVJDStatus(deviceId);
            if (status != VjdStat.VJD_STAT_FREE && status != VjdStat.VJD_STAT_OWN)
            {
                _log.Error(LogPoint.General, $"[vJoy] Device {deviceId} busy. Status: {status}");
                return false;
            }

            if (!_joystick.AcquireVJD(deviceId))
            {
                _log.Error(LogPoint.General, $"[vJoy] Failed to acquire device {deviceId}.");
                return false;
            }

            _log.Info(LogPoint.General, $"[vJoy] Device {deviceId} acquired.");

            if (!_joystick.IsDeviceFfb(deviceId))
            {
                _log.Warning(LogPoint.General, $"[vJoy] Device {deviceId} has no FFB support enabled.");
            }
            else
            {
                // O callback nativo do vJoy só entrega (IntPtr packet, object userData); todo o
                // trabalho de interpretar esse pacote é delegado ao ForceFeedbackHandler, que
                // recebe o handle do dispositivo a cada chamada para poder usar as funções
                // Ffb_h_* de marshaling do SDK.
                _joystick.FfbRegisterGenCB((packet, _) => _ffbHandler.HandlePacket(_joystick!, packet), null);
                _log.Info(LogPoint.General, "[vJoy] FFB registered successfully.");

                _ffbHandler.Start();
            }

            return true;
        }

        public void SetAxis(uint deviceId, HID_USAGES axis, int value)
        {
            _joystick?.SetAxis(value, deviceId, axis);

            if (axis == HID_USAGES.HID_USAGE_X)
            {
                _ffbHandler.UpdateAxisPosition(value);
            }
        }

        public void SetButton(uint deviceId, uint buttonId, bool state)
        {
            _joystick?.SetBtn(state, deviceId, buttonId);
        }

        public void Shutdown(uint deviceId)
        {
            _ffbHandler.Stop();

            _joystick?.RelinquishVJD(deviceId);
            _log.Info(LogPoint.General, $"[vJoy] Device {deviceId} released.");
        }
    }
}