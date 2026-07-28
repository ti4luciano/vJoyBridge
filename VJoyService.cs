// ============================================================================
// VJoyService.cs
// ============================================================================
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using vJoyInterfaceWrap;
using FfbCondition = vJoyInterfaceWrap.vJoy.FFB_EFF_COND;

namespace vJoyBridge
{
    public class VJoyService : IJoystickService
    {
        public event Action<int, int>? OnForceFeedbackReceived;

        private const uint ERROR_SUCCESS = 0;
        private readonly ILogService _log;
        private readonly ForceFeedbackConfig _ffbConfig;
        private readonly AxisRangeConfig _axisConfig;
        private readonly ForceFeedbackHandler _ffbHandler;
        private vJoyInterfaceWrap.vJoy? _joystick;

        private class ConditionEffectState
        {
            public FfbCondition Condition;
            public FFBEType EffectType = FFBEType.ET_NONE;
            public bool Active;
        }

        private readonly object _ffbLock = new();
        private readonly Dictionary<byte, ConditionEffectState> _conditionEffects = new();

        private double _lastNormalizedPos;
        private double _lastVelocity;
        private double _lastAcceleration;
        private long _lastSampleTimestamp;
        private bool _hasPreviousSample;

        public VJoyService(ILogService log, ForceFeedbackConfig ffbConfig, AxisRangeConfig axisConfig)
        {
            _log = log;
            _ffbConfig = ffbConfig;
            _axisConfig = axisConfig;
            _ffbHandler = new ForceFeedbackHandler(_log, _ffbConfig);
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
                _joystick.FfbRegisterGenCB(OnFfbDataReceived, null);
                _log.Info(LogPoint.General, "[vJoy] FFB registered successfully.");
            }

            return true;
        }

        public void SetAxis(uint deviceId, HID_USAGES axis, int value)
        {
            _joystick?.SetAxis(value, deviceId, axis);

            if (axis == HID_USAGES.HID_USAGE_X)
            {
                UpdateAxisKinematicsAndRecalculate(value);
            }
        }

        public void SetButton(uint deviceId, uint buttonId, bool state)
        {
            _joystick?.SetBtn(state, deviceId, buttonId);
        }

        public void Shutdown(uint deviceId)
        {
            _joystick?.RelinquishVJD(deviceId);
            _log.Info(LogPoint.General, $"[vJoy] Device {deviceId} released.");
        }

        private void OnFfbDataReceived(IntPtr packet, object userData)
        {
            FFBPType packetType = FFBPType.PT_EFFREP;
            uint typeResult = _joystick?.Ffb_h_Type(packet, ref packetType) ?? uint.MaxValue;

            _ffbHandler.LogRawPacket(packet, typeResult, packetType);

            if (typeResult != ERROR_SUCCESS) return;

            switch (packetType)
            {
                case FFBPType.PT_CTRLREP:
                    FFB_CTRL control = 0;
                    if (_joystick?.Ffb_h_DevCtrl(packet, ref control) == ERROR_SUCCESS)
                    {
                        _ffbHandler.ProcessDeviceControl(control, DispatchFfb, () => { lock (_ffbLock) _conditionEffects.Clear(); });
                    }
                    break;

                case FFBPType.PT_EFFREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_REPORT effectReport = default;
                    if (_joystick?.Ffb_h_Eff_Report(packet, ref effectReport) == ERROR_SUCCESS)
                    {
                        if (IsConditionEffectType(effectReport.EffectType))
                        {
                            lock (_ffbLock)
                            {
                                GetOrCreateConditionState(effectReport.EffectBlockIndex).EffectType = effectReport.EffectType;
                            }
                        }
                    }
                    break;

                case FFBPType.PT_CONSTREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_CONSTANT constantEffect = default;
                    if (_joystick?.Ffb_h_Eff_Constant(packet, ref constantEffect) == ERROR_SUCCESS)
                    {
                        _ffbHandler.ProcessConstantEffect(constantEffect.Magnitude, DispatchFfb);
                    }
                    break;

                case FFBPType.PT_CONDREP:
                    FfbCondition condition = default;
                    if (_joystick?.Ffb_h_Eff_Cond(packet, ref condition) == ERROR_SUCCESS && !condition.isY)
                    {
                        lock (_ffbLock)
                        {
                            GetOrCreateConditionState(condition.EffectBlockIndex).Condition = condition;
                        }
                        RecalculateConditionEffects();
                    }
                    break;

                case FFBPType.PT_EFOPREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_OP effectOp = default;
                    if (_joystick?.Ffb_h_EffOp(packet, ref effectOp) == ERROR_SUCCESS)
                    {
                        lock (_ffbLock)
                        {
                            if (_conditionEffects.TryGetValue(effectOp.EffectBlockIndex, out var state))
                            {
                                state.Active = effectOp.EffectOp is FFBOP.EFF_START or FFBOP.EFF_SOLO;
                            }
                        }
                        _ffbHandler.ProcessEffectOperation(effectOp.EffectOp, effectOp.EffectBlockIndex, DispatchFfb);
                    }
                    break;

                default:
                    _ffbHandler.LogUnhandledPacket(packetType, packet);
                    break;
            }
        }

        private void DispatchFfb(int pwm, int direction)
        {
            OnForceFeedbackReceived?.Invoke(pwm, direction);
        }

        private ConditionEffectState GetOrCreateConditionState(byte blockIndex)
        {
            if (!_conditionEffects.TryGetValue(blockIndex, out var state))
            {
                state = new ConditionEffectState();
                _conditionEffects[blockIndex] = state;
            }
            return state;
        }

        private static bool IsConditionEffectType(FFBEType type) =>
            type is FFBEType.ET_SPRNG or FFBEType.ET_DMPR or FFBEType.ET_INRT or FFBEType.ET_FRCTN;

        private void UpdateAxisKinematicsAndRecalculate(int rawAxisValue)
        {
            double normalized = NormalizePosition(rawAxisValue);
            long now = Stopwatch.GetTimestamp();

            lock (_ffbLock)
            {
                if (_hasPreviousSample)
                {
                    double dt = (now - _lastSampleTimestamp) / (double)Stopwatch.Frequency;
                    if (dt > 0.0005)
                    {
                        double newVelocity = (normalized - _lastNormalizedPos) / dt;
                        _lastAcceleration = (newVelocity - _lastVelocity) / dt;
                        _lastVelocity = newVelocity;
                    }
                }
                else
                {
                    _hasPreviousSample = true;
                }

                _lastNormalizedPos = normalized;
                _lastSampleTimestamp = now;
            }

            RecalculateConditionEffects();
        }

        private double NormalizePosition(int rawValue)
        {
            int range = _axisConfig.RawMax - _axisConfig.RawMin;
            if (range <= 0) range = 1;

            int clamped = Math.Clamp(rawValue, _axisConfig.RawMin, _axisConfig.RawMax);
            return (((double)(clamped - _axisConfig.RawMin) / range) * 2.0 - 1.0) * 10000.0;
        }

        private void RecalculateConditionEffects()
        {
            double normalizedPos, velocity, acceleration;
            List<(FfbCondition condition, FFBEType type)>? activeEffects = null;

            lock (_ffbLock)
            {
                normalizedPos = _lastNormalizedPos;
                velocity = _lastVelocity;
                acceleration = _lastAcceleration;

                foreach (var state in _conditionEffects.Values)
                {
                    if (!state.Active) continue;
                    (activeEffects ??= newList<(FfbCondition, FFBEType)>()).Add((state.Condition, state.EffectType));
                }
            }

            if (activeEffects == null || activeEffects.Count == 0) return;

            double totalForce = 0;
            foreach (var (condition, type) in activeEffects)
            {
                double metric = type switch
                {
                    FFBEType.ET_DMPR or FFBEType.ET_FRCTN => velocity * _ffbConfig.VelocityScale,
                    FFBEType.ET_INRT => acceleration * _ffbConfig.AccelerationScale,
                    _ => normalizedPos
                };

                totalForce += CalculateConditionForce(condition, metric);
            }

            totalForce = Math.Clamp(totalForce, -10000.0, 10000.0) * _ffbConfig.MagnitudeMultiplier;

            int pwm = Math.Clamp((int)Math.Round(Math.Abs(totalForce) * 255.0 / 10000.0), 0, 255);
            int direction = totalForce >= 0 ? 1 : 0;

            DispatchFfb(pwm, direction);
        }

        private static double CalculateConditionForce(FfbCondition condition, double metric)
        {
            double deadLow = condition.CenterPointOffset - condition.DeadBand;
            double deadHigh = condition.CenterPointOffset + condition.DeadBand;

            double force;
            if (metric < deadLow) force = (metric - deadLow) * condition.NegCoeff / 10000.0;
            else if (metric > deadHigh) force = (metric - deadHigh) * condition.PosCoeff / 10000.0;
            else force = 0.0;

            return force >= 0
                ? Math.Min(force, (double)condition.PosSatur)
                : Math.Max(force, -(double)condition.NegSatur);
        }
    }
}