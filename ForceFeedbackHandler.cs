// ============================================================================
// ForceFeedbackHandler.cs
// ============================================================================
using System;
using vJoyInterfaceWrap;

namespace vJoyBridge
{
    public class ForceFeedbackHandler
    {
        private readonly ILogService _log;
        private readonly ForceFeedbackConfig _config;

        public ForceFeedbackHandler(ILogService log, ForceFeedbackConfig config)
        {
            _log = log;
            _config = config;
        }

        public void LogRawPacket(IntPtr packet, uint typeResult, FFBPType packetType)
        {
            _log.Debug(LogPoint.VJoyEvents, $"[FFB Packet] Ptr: 0x{packet.ToInt64():X} | Result: {typeResult} | Type: {packetType}");
        }

        public void ProcessDeviceControl(FFB_CTRL control, Action<int, int> ffbCallback, Action resetConditionEffects)
        {
            _log.Info(LogPoint.VJoyEvents, $"[FFB Control] Device Command: {control}");

            if (control == FFB_CTRL.CTRL_STOPALL)
            {
                _log.Info(LogPoint.VJoyEvents, "[FFB Control] STOP ALL triggered -> PWM: 0");
                ffbCallback?.Invoke(0, 0);
            }
            else if (control == FFB_CTRL.CTRL_DEVRST)
            {
                resetConditionEffects?.Invoke();
                _log.Info(LogPoint.VJoyEvents, "[FFB Control] RESET triggered -> PWM: 0");
                ffbCallback?.Invoke(0, 0);
            }
        }

        public void ProcessConstantEffect(int magnitude, Action<int, int> ffbCallback)
        {
            int adjustedMagnitude = (int)Math.Round(magnitude * _config.MagnitudeMultiplier);
            int pwm = Math.Clamp(Math.Abs(adjustedMagnitude) * 255 / 10000, 0, 255);
            int direction = adjustedMagnitude >= 0 ? 1 : 0;

            _log.Info(LogPoint.VJoyEvents, $"[FFB Constant] Raw Mag: {magnitude} | Adjusted: {adjustedMagnitude} | PWM: {pwm} | Dir: {direction}");
            ffbCallback?.Invoke(pwm, direction);
        }

        public void ProcessEffectOperation(FFBOP operation, byte blockIndex, Action<int, int> ffbCallback)
        {
            _log.Info(LogPoint.VJoyEvents, $"[FFB Operation] Op: {operation} | Block: {blockIndex}");

            if (operation == FFBOP.EFF_STOP)
            {
                _log.Info(LogPoint.VJoyEvents, $"[FFB Operation] STOP effect block {blockIndex} -> PWM: 0");
                ffbCallback?.Invoke(0, 0);
            }
        }

        public void LogUnhandledPacket(FFBPType packetType, IntPtr packet)
        {
            _log.Warning(LogPoint.VJoyEvents, $"[FFB Unhandled] Unrecognized packet type: {packetType} (0x{packet.ToInt64():X})");
        }
    }
}