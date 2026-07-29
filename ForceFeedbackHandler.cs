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

        public void ProcessDeviceControl(FFB_CTRL control, Action<int, int> ffbCallback, Action resetConditionEffects, Action resetGain)
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
                resetGain?.Invoke();
                _log.Info(LogPoint.VJoyEvents, "[FFB Control] RESET triggered -> PWM: 0, Gain: 255");
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

        public void ProcessPeriodicEffect(byte blockIndex, FFBEType effectType, uint magnitude, int offset, uint phase, uint period)
        {
            _log.Info(LogPoint.VJoyEvents, $"[FFB Periodic] Block: {blockIndex} | Type: {effectType} | Magnitude: {magnitude} | Offset: {offset} | Phase: {phase} | Period: {period}");
        }

        public void LogEffectBlockAllocated(byte blockIndex)
        {
            _log.Info(LogPoint.VJoyEvents, $"[FFB Block] New effect block allocated: {blockIndex}");
        }

        public void LogEffectBlockFreed(byte blockIndex)
        {
            _log.Info(LogPoint.VJoyEvents, $"[FFB Block] Effect block freed: {blockIndex} -> clearing cached state");
        }

        public void ProcessDeviceGain(byte gain)
        {
            double percent = gain / 255.0 * 100.0;
            _log.Info(LogPoint.VJoyEvents, $"[FFB Gain] Global gain set to {gain}/255 ({percent:F0}%)");
        }
    }
}