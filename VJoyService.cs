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

            // Parameters for periodic effects (square/sine/triangle/sawtooth), populated from PT_PRIDREP.
            public uint PeriodicMagnitude;
            public int PeriodicOffset;
            public uint PeriodicPhase;
            public uint PeriodicPeriod;

            // Timestamp (Stopwatch ticks) of the last time this block transitioned to Active,
            // used as the time origin for periodic waveform playback.
            public long ActivationTimestamp;
        }

        // Snapshot of an active effect's parameters, taken while holding _ffbLock so the
        // subsequent force calculation can run lock-free.
        private readonly struct EffectSnapshot
        {
            public FFBEType Type { get; init; }
            public FfbCondition Condition { get; init; }
            public uint PeriodicMagnitude { get; init; }
            public int PeriodicOffset { get; init; }
            public uint PeriodicPhase { get; init; }
            public uint PeriodicPeriod { get; init; }
            public long ActivationTimestamp { get; init; }
        }

        private readonly object _ffbLock = new();
        private readonly Dictionary<byte, ConditionEffectState> _conditionEffects = new();

        private double _lastNormalizedPos;
        private double _lastVelocity;
        private double _lastAcceleration;
        private long _lastSampleTimestamp;
        private bool _hasPreviousSample;

        // Periodic effects (square/sine/triangle/sawtooth) are time-based waveforms and must keep
        // producing force even when the axis isn't moving and no new FFB packet arrives, so we
        // tick the recalculation on a timer instead of relying solely on packet/axis events.
        private System.Threading.Timer? _periodicEffectTimer;
        private const int PeriodicEffectTickMs = 10;

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

                _periodicEffectTimer = new System.Threading.Timer(_ => RecalculateConditionEffects(), null, PeriodicEffectTickMs, PeriodicEffectTickMs);
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
            _periodicEffectTimer?.Dispose();
            _periodicEffectTimer = null;

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
                        if (IsConditionEffectType(effectReport.EffectType) || IsPeriodicEffectType(effectReport.EffectType))
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
                                bool wasActive = state.Active;
                                state.Active = effectOp.EffectOp is FFBOP.EFF_START or FFBOP.EFF_SOLO;

                                // Reset the waveform's time origin every time the effect (re)starts,
                                // so periodic effects (sine/square/etc.) restart from phase 0.
                                if (state.Active && !wasActive)
                                {
                                    state.ActivationTimestamp = Stopwatch.GetTimestamp();
                                }
                            }
                        }
                        _ffbHandler.ProcessEffectOperation(effectOp.EffectOp, effectOp.EffectBlockIndex, DispatchFfb);
                    }
                    break;

                case FFBPType.PT_PRIDREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_PERIOD periodicEffect = default;
                    if (_joystick?.Ffb_h_Eff_Period(packet, ref periodicEffect) == ERROR_SUCCESS)
                    {
                        FFBEType periodicType;
                        lock (_ffbLock)
                        {
                            var state = GetOrCreateConditionState(periodicEffect.EffectBlockIndex);
                            state.PeriodicMagnitude = periodicEffect.Magnitude;
                            state.PeriodicOffset = periodicEffect.Offset;
                            state.PeriodicPhase = periodicEffect.Phase;
                            state.PeriodicPeriod = periodicEffect.Period;
                            periodicType = state.EffectType;
                        }

                        _ffbHandler.ProcessPeriodicEffect(periodicEffect.EffectBlockIndex, periodicType,
                            periodicEffect.Magnitude, periodicEffect.Offset, periodicEffect.Phase, periodicEffect.Period);

                        RecalculateConditionEffects();
                    }
                    break;

                case FFBPType.PT_NEWEFREP:
                    int newBlockIndex = -1;
                    if (_joystick?.Ffb_h_EBI(packet, ref newBlockIndex) == ERROR_SUCCESS && newBlockIndex >= 0)
                    {
                        // The device can reuse a block index that was previously freed. Wipe any
                        // stale cached state (old Active flag, condition, or periodic params) so it
                        // can't leak into the new effect that's about to be configured on this block.
                        lock (_ffbLock)
                        {
                            _conditionEffects.Remove((byte)newBlockIndex);
                        }
                        _ffbHandler.LogEffectBlockAllocated((byte)newBlockIndex);
                    }
                    break;

                case FFBPType.PT_BLKFRREP:
                    int freedBlockIndex = -1;
                    if (_joystick?.Ffb_h_EBI(packet, ref freedBlockIndex) == ERROR_SUCCESS && freedBlockIndex >= 0)
                    {
                        // Stop and forget this block immediately. If it stayed in the dictionary
                        // while still marked Active, it kept contributing force in
                        // RecalculateConditionEffects() forever, even after the game freed it -
                        // this is what produced the "stuck spinning" behavior seen in testing.
                        lock (_ffbLock)
                        {
                            _conditionEffects.Remove((byte)freedBlockIndex);
                        }
                        _ffbHandler.LogEffectBlockFreed((byte)freedBlockIndex);
                        RecalculateConditionEffects();
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

        private static bool IsPeriodicEffectType(FFBEType type) =>
            type is FFBEType.ET_SQR or FFBEType.ET_SINE or FFBEType.ET_TRNGL or FFBEType.ET_STUP or FFBEType.ET_STDN;

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
            List<EffectSnapshot>? activeEffects = null;

            lock (_ffbLock)
            {
                normalizedPos = _lastNormalizedPos;
                velocity = _lastVelocity;
                acceleration = _lastAcceleration;

                foreach (var state in _conditionEffects.Values)
                {
                    if (!state.Active) continue;
                    (activeEffects ??= new List<EffectSnapshot>()).Add(new EffectSnapshot
                    {
                        Type = state.EffectType,
                        Condition = state.Condition,
                        PeriodicMagnitude = state.PeriodicMagnitude,
                        PeriodicOffset = state.PeriodicOffset,
                        PeriodicPhase = state.PeriodicPhase,
                        PeriodicPeriod = state.PeriodicPeriod,
                        ActivationTimestamp = state.ActivationTimestamp
                    });
                }
            }

            if (activeEffects == null || activeEffects.Count == 0) return;

            double totalForce = 0;
            foreach (var effect in activeEffects)
            {
                if (IsPeriodicEffectType(effect.Type))
                {
                    totalForce += CalculatePeriodicForce(effect.Type, effect.PeriodicMagnitude, effect.PeriodicOffset,
                        effect.PeriodicPhase, effect.PeriodicPeriod, effect.ActivationTimestamp);
                    continue;
                }

                double metric = effect.Type switch
                {
                    FFBEType.ET_DMPR or FFBEType.ET_FRCTN => velocity * _ffbConfig.VelocityScale,
                    FFBEType.ET_INRT => acceleration * _ffbConfig.AccelerationScale,
                    _ => normalizedPos
                };

                totalForce += CalculateConditionForce(effect.Condition, metric);
            }

            totalForce = Math.Clamp(totalForce, -10000.0, 10000.0) * _ffbConfig.MagnitudeMultiplier;

            int pwm = Math.Clamp((int)Math.Round(Math.Abs(totalForce) * 255.0 / 10000.0), 0, 255);
            int direction = totalForce >= 0 ? 1 : 0;

            DispatchFfb(pwm, direction);
        }

        // Periodic effects (square/sine/triangle/sawtooth) describe a waveform in time rather than
        // a response to axis position/velocity, so the force is derived from elapsed time since the
        // effect last started (Start/Solo), the reported Period/Phase, and Magnitude/Offset.
        private static double CalculatePeriodicForce(FFBEType type, uint magnitude, int offset, uint phase, uint period, long activationTimestamp)
        {
            if (period == 0) return offset;

            double elapsedMs = (Stopwatch.GetTimestamp() - activationTimestamp) * 1000.0 / Stopwatch.Frequency;
            double phaseFraction = (phase / 100.0) / 360.0; // Phase is reported in hundredths of a degree (0-35999)

            double cyclePos = (elapsedMs / period) + phaseFraction;
            cyclePos -= Math.Floor(cyclePos); // wrap into [0, 1)

            double waveform = type switch
            {
                FFBEType.ET_SINE => Math.Sin(cyclePos * 2.0 * Math.PI),
                FFBEType.ET_SQR => cyclePos < 0.5 ? 1.0 : -1.0,
                FFBEType.ET_TRNGL => 1.0 - 4.0 * Math.Abs(cyclePos - 0.5),
                FFBEType.ET_STUP => cyclePos * 2.0 - 1.0,
                FFBEType.ET_STDN => 1.0 - cyclePos * 2.0,
                _ => 0.0
            };

            return Math.Clamp(offset + waveform * magnitude, -10000.0, 10000.0);
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