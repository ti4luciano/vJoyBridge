#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using vJoyInterfaceWrap;
using FfbCondition = vJoyInterfaceWrap.vJoy.FFB_EFF_COND;

namespace vJoyBridge
{
    /// <summary>
    /// Implementação concreta do serviço do vJoy usando o Wrapper nativo e chamadas diretas P/Invoke.
    /// Resolve incompatibilidades clássicas de FFB presentes em wrappers desatualizados.
    /// </summary>
    public class VJoyService : IJoystickService
    {
        public event Action<int, int>? OnForceFeedbackReceived;

        private readonly ILogService _log;
        private readonly ForceFeedbackConfig _ffbConfig;
        private readonly ForceFeedbackHandler _ffbHandler;
        private vJoyInterfaceWrap.vJoy? _joystick;

        private const uint ERROR_SUCCESS = 0;

        /// <summary>
        /// Representa o estado completo de um bloco de efeito FFB no vJoy.
        /// </summary>
        private class EffectBlockState
        {
            public byte BlockIndex;
            public FFBEType EffectType = FFBEType.ET_NONE;
            public bool Active;

            // Efeito Constante
            public int ConstantMagnitude;

            // Efeito de Condição (Spring, Damper, Friction, Inertia)
            public FfbCondition Condition;

            // Efeito Periódico (Sine, Square, Triangle, Sawtooth)
            public uint PeriodicMagnitude;
            public int PeriodicOffset;
            public uint PeriodicPhase;
            public uint PeriodicPeriod;

            // Cronômetro para cálculo do tempo decorrido em efeitos periódicos
            public readonly Stopwatch Stopwatch = new();
        }

        private readonly object _ffbLock = new();
        private readonly Dictionary<byte, EffectBlockState> _effects = new();
        private byte _globalGain = 255; // 0..255 (Padrão 100%)

        // --- Cinemática do eixo X (volante) ---
        private int _lastAxisPositionX = 16384; // Centro (0..32767)
        private double _lastNormalizedPos;
        private double _lastVelocity;
        private double _lastAcceleration;
        private long _lastSampleTimestamp;
        private bool _hasPreviousSample;

        // --- Timer para atualização contínua de efeitos periódicos (10ms / 100Hz) ---
        private readonly Timer _periodicTimer;

        public VJoyService(ILogService log, ForceFeedbackConfig ffbConfig, ForceFeedbackHandler? ffbHandler = null)
        {
            _log = log;
            _ffbConfig = ffbConfig;
            _ffbHandler = ffbHandler ?? new ForceFeedbackHandler(log, ffbConfig);

            // Timer de 10ms para reavaliar a força quando há efeitos periódicos em execução
            _periodicTimer = new Timer(OnPeriodicTimerTick, null, 10, 10);
        }

        public bool Initialize(uint deviceId)
        {
            _joystick = new vJoyInterfaceWrap.vJoy();

            if (!_joystick.vJoyEnabled())
            {
                _log.Error(LogPoint.General, "[vJoy] Erro: Driver não encontrado ou DLL ausente.");
                return false;
            }

            VjdStat status = _joystick.GetVJDStatus(deviceId);
            if (status != VjdStat.VJD_STAT_FREE && status != VjdStat.VJD_STAT_OWN)
            {
                _log.Error(LogPoint.General, $"[vJoy] Erro: Dispositivo {deviceId} ocupado. Status: {status}");
                return false;
            }

            if (!_joystick.AcquireVJD(deviceId))
            {
                _log.Error(LogPoint.General, $"[vJoy] Erro ao adquirir o dispositivo {deviceId}.");
                return false;
            }

            _log.Info(LogPoint.General, $"[vJoy] Dispositivo {deviceId} adquirido com sucesso.");

            if (!_joystick.IsDeviceFfb(deviceId))
            {
                _log.Warning(LogPoint.General, $"[vJoy] ATENÇÃO: Dispositivo {deviceId} não está configurado para suportar FFB no painel do vJoy.");
            }
            else
            {
                _joystick.FfbRegisterGenCB(OnFfbDataReceived, null);
                _log.Info(LogPoint.General, "[vJoy] Canal FFB registrado via wrapper vJoyInterfaceWrap.");
            }

            return true;
        }

        public void SetAxis(uint deviceId, HID_USAGES axis, int value)
        {
            int safeValue = Math.Clamp(value, 0, 32767);
            _joystick?.SetAxis(safeValue, deviceId, axis);

            if (axis == HID_USAGES.HID_USAGE_X)
            {
                UpdateAxisKinematicsAndRecalculate(safeValue);
            }
        }

        public void Shutdown(uint deviceId)
        {
            _periodicTimer.Dispose();
            _joystick?.RelinquishVJD(deviceId);
            _log.Info(LogPoint.General, $"[vJoy] Dispositivo {deviceId} liberado.");
        }

        /// <summary>
        /// Callback executado a cada tick do timer de 10ms.
        /// Se houver efeitos periódicos ativos, recalcula e recalibra a força final enviada ao motor.
        /// </summary>
        private void OnPeriodicTimerTick(object? state)
        {
            bool hasActivePeriodic;
            lock (_ffbLock)
            {
                hasActivePeriodic = HasActivePeriodicEffectsUnsafe();
            }

            if (hasActivePeriodic)
            {
                RecalculateTotalForce();
            }
        }

        private bool HasActivePeriodicEffectsUnsafe()
        {
            foreach (var effect in _effects.Values)
            {
                if (effect.Active && IsPeriodicEffectType(effect.EffectType))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Callback principal de FFB do vJoy. Roteia os pacotes e delega tratamento/logs para o ForceFeedbackHandler.
        /// </summary>
        private void OnFfbDataReceived(IntPtr packet, object userData)
        {
            FFBPType packetType = FFBPType.PT_EFFREP;
            uint typeResult = _joystick?.Ffb_h_Type(packet, ref packetType) ?? uint.MaxValue;

            _ffbHandler.LogRawPacket(packet, typeResult, packetType);

            if (typeResult != ERROR_SUCCESS)
            {
                _log.Warning(LogPoint.VJoyEvents, $"[vJoy FFB] Falha ao obter o tipo do pacote (Ffb_h_Type retornou {typeResult}). Pacote ignorado.");
                return;
            }

            switch (packetType)
            {
                case FFBPType.PT_CTRLREP:
                    FFB_CTRL control = 0;
                    if (_joystick?.Ffb_h_DevCtrl(packet, ref control) == ERROR_SUCCESS)
                    {
                        _ffbHandler.ProcessDeviceControl(control,
                            onStopAll: () =>
                            {
                                lock (_ffbLock)
                                {
                                    foreach (var state in _effects.Values)
                                    {
                                        state.Active = false;
                                        state.Stopwatch.Stop();
                                    }
                                }
                                RecalculateTotalForce();
                            },
                            onReset: () =>
                            {
                                lock (_ffbLock)
                                {
                                    _effects.Clear();
                                    _globalGain = 255;
                                }
                                RecalculateTotalForce();
                            });
                    }
                    else
                    {
                        _log.Warning(LogPoint.VJoyEvents, "[vJoy FFB] PT_CTRLREP recebido, mas Ffb_h_DevCtrl falhou ao decodificar.");
                    }
                    break;

                case FFBPType.PT_GAINREP:
                case FFBPType.PT_SETGAINREP:
                    byte gain = 255;
                    if (_joystick?.Ffb_h_DevGain(packet, ref gain) == ERROR_SUCCESS)
                    {
                        lock (_ffbLock)
                        {
                            _globalGain = gain;
                        }
                        _ffbHandler.ProcessDeviceGain(gain);
                        RecalculateTotalForce();
                    }
                    else
                    {
                        _log.Warning(LogPoint.VJoyEvents, "[vJoy FFB] PT_GAINREP recebido, mas Ffb_h_DevGain falhou ao decodificar.");
                    }
                    break;

                case FFBPType.PT_EFFREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_REPORT effectReport = default;
                    if (_joystick?.Ffb_h_Eff_Report(packet, ref effectReport) == ERROR_SUCCESS)
                    {
                        _ffbHandler.ProcessEffectReport(effectReport.EffectBlockIndex, effectReport.EffectType, effectReport.Direction);

                        lock (_ffbLock)
                        {
                            var state = GetOrCreateEffectState(effectReport.EffectBlockIndex);
                            state.EffectType = effectReport.EffectType;
                        }
                    }
                    else
                    {
                        _log.Warning(LogPoint.VJoyEvents, "[vJoy FFB] PT_EFFREP recebido, mas Ffb_h_Eff_Report falhou ao decodificar.");
                    }
                    break;

                case FFBPType.PT_CONSTREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_CONSTANT constantEffect = default;
                    if (_joystick?.Ffb_h_Eff_Constant(packet, ref constantEffect) == ERROR_SUCCESS)
                    {
                        _ffbHandler.ProcessConstantEffect(constantEffect.Magnitude);

                        lock (_ffbLock)
                        {
                            var state = GetOrCreateEffectState(constantEffect.EffectBlockIndex);
                            state.EffectType = FFBEType.ET_CONST;
                            state.ConstantMagnitude = constantEffect.Magnitude;
                            state.Active = true;
                        }

                        RecalculateTotalForce();
                    }
                    else
                    {
                        _log.Warning(LogPoint.VJoyEvents, "[vJoy FFB] PT_CONSTREP recebido, mas Ffb_h_Eff_Constant falhou ao decodificar.");
                    }
                    break;

                case FFBPType.PT_CONDREP:
                    FfbCondition condition = default;
                    if (_joystick?.Ffb_h_Eff_Cond(packet, ref condition) == ERROR_SUCCESS)
                    {
                        if (!condition.isY)
                        {
                            lock (_ffbLock)
                            {
                                var state = GetOrCreateEffectState(condition.EffectBlockIndex);
                                state.Condition = condition;
                                _ffbHandler.ProcessConditionEffect(condition, state.EffectType);
                            }

                            RecalculateTotalForce();
                        }
                        else
                        {
                            _log.Debug(LogPoint.VJoyEvents, "[vJoy FFB] Condição no eixo Y ignorada (bridge só trata eixo X/rotação).");
                        }
                    }
                    else
                    {
                        _log.Warning(LogPoint.VJoyEvents, "[vJoy FFB] PT_CONDREP recebido, mas Ffb_h_Eff_Cond falhou ao decodificar.");
                    }
                    break;

                case FFBPType.PT_PRIDREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_PERIOD periodicEffect = default;
                    if (_joystick?.Ffb_h_Eff_Period(packet, ref periodicEffect) == ERROR_SUCCESS)
                    {
                        lock (_ffbLock)
                        {
                            var state = GetOrCreateEffectState(periodicEffect.EffectBlockIndex);
                            state.PeriodicMagnitude = periodicEffect.Magnitude;
                            state.PeriodicOffset = (int)periodicEffect.Offset;
                            state.PeriodicPhase = periodicEffect.Phase;
                            state.PeriodicPeriod = periodicEffect.Period;

                            _ffbHandler.ProcessPeriodicEffect(
                                periodicEffect.EffectBlockIndex,
                                state.EffectType,
                                periodicEffect.Magnitude,
                                (int)periodicEffect.Offset,
                                periodicEffect.Phase,
                                periodicEffect.Period);
                        }

                        RecalculateTotalForce();
                    }
                    else
                    {
                        _log.Warning(LogPoint.VJoyEvents, "[vJoy FFB] PT_PRIDREP recebido, mas Ffb_h_Eff_Period falhou ao decodificar.");
                    }
                    break;

                case FFBPType.PT_EFOPREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_OP effectOp = default;
                    if (_joystick?.Ffb_h_EffOp(packet, ref effectOp) == ERROR_SUCCESS)
                    {
                        lock (_ffbLock)
                        {
                            if (_effects.TryGetValue(effectOp.EffectBlockIndex, out var state))
                            {
                                _ffbHandler.ProcessEffectOperation(effectOp.EffectOp, effectOp.EffectBlockIndex,
                                    onStart: () =>
                                    {
                                        state.Active = true;
                                        state.Stopwatch.Restart();
                                    },
                                    onStop: () =>
                                    {
                                        state.Active = false;
                                        state.Stopwatch.Stop();
                                    });
                            }
                        }

                        RecalculateTotalForce();
                    }
                    else
                    {
                        _log.Warning(LogPoint.VJoyEvents, "[vJoy FFB] PT_EFOPREP recebido, mas Ffb_h_EffOp falhou ao decodificar.");
                    }
                    break;

                default:
                    _ffbHandler.LogUnhandledPacket(packetType, packet);
                    break;
            }
        }

        private EffectBlockState GetOrCreateEffectState(byte effectBlockIndex)
        {
            if (!_effects.TryGetValue(effectBlockIndex, out var state))
            {
                state = new EffectBlockState { BlockIndex = effectBlockIndex };
                _effects[effectBlockIndex] = state;
            }
            return state;
        }

        private static bool IsConditionEffectType(FFBEType type) =>
            type is FFBEType.ET_SPRNG or FFBEType.ET_DMPR or FFBEType.ET_INRT or FFBEType.ET_FRCTN;

        private static bool IsPeriodicEffectType(FFBEType type) =>
            type is FFBEType.ET_SINE or FFBEType.ET_SQR or FFBEType.ET_TRI or FFBEType.ET_SAWT or FFBEType.ET_SAWD;

        private void UpdateAxisKinematicsAndRecalculate(int axisValue)
        {
            double normalized = NormalizePosition(axisValue);
            long now = Stopwatch.GetTimestamp();

            lock (_ffbLock)
            {
                _lastAxisPositionX = axisValue;

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

            RecalculateTotalForce();
        }

        private static double NormalizePosition(int axisValue) => ((axisValue - 16383.5) / 16383.5) * 10000.0;

        /// <summary>
        /// Recalcula a força total somando Constant, Condições e Periódicos ativos.
        /// Aplica Ganho Global e repassa os valores calculados de PWM e Direção para o evento de saída.
        /// </summary>
        private void RecalculateTotalForce()
        {
            double normalizedPosition, velocity, acceleration;
            byte globalGain;
            List<EffectBlockState> activeSnapshot = new();

            lock (_ffbLock)
            {
                normalizedPosition = _lastNormalizedPos;
                velocity = _lastVelocity;
                acceleration = _lastAcceleration;
                globalGain = _globalGain;

                foreach (var state in _effects.Values)
                {
                    if (state.Active)
                    {
                        activeSnapshot.Add(state);
                    }
                }
            }

            if (activeSnapshot.Count == 0)
            {
                OnForceFeedbackReceived?.Invoke(0, 0);
                return;
            }

            double totalRawForce = 0;

            foreach (var state in activeSnapshot)
            {
                if (state.EffectType == FFBEType.ET_CONST)
                {
                    totalRawForce += state.ConstantMagnitude;
                }
                else if (IsConditionEffectType(state.EffectType) || state.EffectType == FFBEType.ET_NONE)
                {
                    double metric = state.EffectType switch
                    {
                        FFBEType.ET_DMPR or FFBEType.ET_FRCTN => velocity * _ffbConfig.VelocityScale,
                        FFBEType.ET_INRT => acceleration * _ffbConfig.AccelerationScale,
                        _ => normalizedPosition // ET_SPRNG ou ainda sem tipo definido
                    };

                    totalRawForce += _ffbHandler.CalculateConditionForce(state.Condition, metric);
                }
                else if (IsPeriodicEffectType(state.EffectType))
                {
                    long elapsedMs = state.Stopwatch.ElapsedMilliseconds;
                    totalRawForce += _ffbHandler.CalculatePeriodicForce(
                        state.EffectType,
                        state.PeriodicMagnitude,
                        state.PeriodicOffset,
                        state.PeriodicPhase,
                        state.PeriodicPeriod,
                        elapsedMs);
                }
            }

            _ffbHandler.CalculateFinalForce(totalRawForce, globalGain, out int pwm, out int direction, out double finalForce);
            _ffbHandler.LogForceCalculation(finalForce, pwm, direction, activeSnapshot.Count, globalGain);

            OnForceFeedbackReceived?.Invoke(pwm, direction);
        }
    }
}