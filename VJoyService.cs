#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private vJoyInterfaceWrap.vJoy? _joystick;

        // =========================================================================
        // --- P/Invoke Direto para a vJoyInterface.dll (Nativo C++) ---
        // Bypassa as limitações de FFB do wrapper intermediário vJoyInterfaceWrap.dll
        // =========================================================================

        private const uint ERROR_SUCCESS = 0;

        /// <summary>Estado de um efeito de Condição (Spring/Damper/Inertia/Friction) ativo no eixo X.</summary>
        private class ConditionEffectState
        {
            public FfbCondition Condition;
            public FFBEType EffectType = FFBEType.ET_NONE;
            public bool Active;
        }

        // --- Estado dos efeitos de Condição no eixo X, indexados por EffectBlockIndex ---
        // A bridge trata apenas o eixo X (rotação do volante). Se o jogo tocar mais de um
        // efeito de condição ao mesmo tempo (ex: Spring + Damper em blocos diferentes),
        // as forças de todos os efeitos ATIVOS são somadas antes de virar PWM.
        private readonly object _ffbLock = new();
        private readonly Dictionary<byte, ConditionEffectState> _conditionEffects = new();

        // --- Cinemática do eixo X, usada para alimentar Spring (posição), Damper/Friction
        // (velocidade) e Inertia (aceleração) ---
        private int _lastAxisPositionX = 16384; // centro (0..32767) até chegar o 1º valor real
        private double _lastNormalizedPos;
        private double _lastVelocity;
        private double _lastAcceleration;
        private long _lastSampleTimestamp;
        private bool _hasPreviousSample;

        public VJoyService(ILogService log, ForceFeedbackConfig ffbConfig)
        {
            _log = log;
            _ffbConfig = ffbConfig;
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

            // Inicialização do FFB Real
            if (!_joystick.IsDeviceFfb(deviceId))
            {
                _log.Warning(LogPoint.General, $"[vJoy] ATENÇÃO: Dispositivo {deviceId} não está configurado para suportar FFB no painel do vJoy.");
            }
            else
            {
                // Registra o callback usando a API do wrapper vJoyInterfaceWrap.
                // Esta versão do vJoy expõe os helpers Ffb_h_* e evita o EntryPointNotFoundException.
                _joystick.FfbRegisterGenCB(OnFfbDataReceived, null);
                _log.Info(LogPoint.General, "[vJoy] Canal FFB registrado via wrapper vJoyInterfaceWrap.");
            }

            return true;
        }

        public void SetAxis(uint deviceId, HID_USAGES axis, int value)
        {
            int safeValue = Math.Clamp(value, 0, 32767);
            _joystick?.SetAxis(safeValue, deviceId, axis);

            // O eixo X representa a rotação do volante: toda vez que ele se move,
            // atualizamos posição/velocidade/aceleração e recalculamos os efeitos ativos.
            if (axis == HID_USAGES.HID_USAGE_X)
            {
                UpdateAxisKinematicsAndRecalculate(safeValue);
            }
        }

        public void Shutdown(uint deviceId)
        {
            _joystick?.RelinquishVJD(deviceId);
            _log.Info(LogPoint.General, $"[vJoy] Dispositivo {deviceId} liberado.");
        }

        /// <summary>
        /// Callback de FFB interpretando os pacotes pelo wrapper oficial do vJoy.
        /// Todo pacote que chega passa primeiro pelo log "raiz" abaixo, antes de
        /// ser roteado para o case correspondente — isso permite diagnosticar
        /// pacotes que não estejam sendo interpretados corretamente.
        /// </summary>
        private void OnFfbDataReceived(IntPtr packet, object userData)
        {
            FFBPType packetType = FFBPType.PT_EFFREP;
            uint typeResult = _joystick?.Ffb_h_Type(packet, ref packetType) ?? uint.MaxValue;

            // --- Log raiz: todo evento que chega, antes de qualquer interpretação ---
            _log.Debug(LogPoint.VJoyEvents, $"[vJoy FFB] Pacote recebido | ptr=0x{packet.ToInt64():X} | Ffb_h_Type retorno={typeResult} | Tipo={packetType}");

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
                        _log.Info(LogPoint.VJoyEvents, $"[vJoy FFB] ---> CONTROLE DE DISPOSITIVO: {control}");

                        if (control == FFB_CTRL.CTRL_STOPALL)
                        {
                            // Para tudo, mas mantém os efeitos configurados (só desativa).
                            lock (_ffbLock)
                            {
                                foreach (var state in _conditionEffects.Values) state.Active = false;
                            }
                            _log.Info(LogPoint.VJoyEvents, "[Bridge -> STM32] PARADA DE EMERGÊNCIA! Enviando PWM: 0");
                            OnForceFeedbackReceived?.Invoke(0, 0);
                        }
                        else if (control == FFB_CTRL.CTRL_DEVRST)
                        {
                            // Reset limpa todos os efeitos da memória do dispositivo.
                            lock (_ffbLock) { _conditionEffects.Clear(); }
                            _log.Info(LogPoint.VJoyEvents, "[Bridge -> STM32] RESET! Efeitos limpos. Enviando PWM: 0");
                            OnForceFeedbackReceived?.Invoke(0, 0);
                        }
                    }
                    else
                    {
                        _log.Warning(LogPoint.VJoyEvents, "[vJoy FFB] PT_CTRLREP recebido, mas Ffb_h_DevCtrl falhou ao decodificar.");
                    }
                    break;

                case FFBPType.PT_EFFREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_REPORT effectReport = default;
                    if (_joystick?.Ffb_h_Eff_Report(packet, ref effectReport) == ERROR_SUCCESS)
                    {
                        _log.Info(LogPoint.VJoyEvents,
                            $"[vJoy FFB] ---> NOVO EFEITO CONFIGURADO | Bloco: {effectReport.EffectBlockIndex} | Tipo: {effectReport.EffectType} | Direção: {effectReport.Direction}");

                        // Registra/atualiza o TIPO do efeito de condição para este bloco.
                        // É esse tipo que diz se o PT_CONDREP correspondente deve reagir a
                        // posição (Spring), velocidade (Damper/Friction) ou aceleração (Inertia).
                        if (IsConditionEffectType(effectReport.EffectType))
                        {
                            lock (_ffbLock)
                            {
                                GetOrCreateConditionState(effectReport.EffectBlockIndex).EffectType = effectReport.EffectType;
                            }
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
                        int magnitude = constantEffect.Magnitude;
                        int magnitudeAjustada = (int)Math.Round(magnitude * _ffbConfig.MagnitudeMultiplier);

                        int pwm = Math.Abs(magnitudeAjustada) * 255 / 10000;
                        pwm = Math.Clamp(pwm, 0, 255);

                        int direction = magnitudeAjustada >= 0 ? 1 : 0;

                        _log.Info(LogPoint.VJoyEvents,
                            $"[vJoy FFB] ---> EFEITO CONSTANTE ATUALIZADO | Mag original: {magnitude} | " +
                            $"Multiplicador: {_ffbConfig.MagnitudeMultiplier} | Mag ajustada: {magnitudeAjustada} | " +
                            $"PWM: {pwm} | Dir: {direction}");

                        // Envia para o motor
                        _log.Debug(LogPoint.VJoyEvents, $"[Bridge -> STM32] Enviando: F:{pwm},{direction}");
                        OnForceFeedbackReceived?.Invoke(pwm, direction);
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
                        _log.Info(LogPoint.VJoyEvents,
                            $"[vJoy FFB] ---> CONDIÇÃO RECEBIDA | Bloco: {condition.EffectBlockIndex} | Eixo: {(condition.isY ? "Y" : "X")} | " +
                            $"Centro: {condition.CenterPointOffset} | PosCoeff: {condition.PosCoeff} | NegCoeff: {condition.NegCoeff} | " +
                            $"PosSat: {condition.PosSatur} | NegSat: {condition.NegSatur} | DeadBand: {condition.DeadBand}");

                        if (!condition.isY)
                        {
                            FFBEType tipoConhecido;
                            lock (_ffbLock)
                            {
                                var state = GetOrCreateConditionState(condition.EffectBlockIndex);
                                state.Condition = condition;
                                tipoConhecido = state.EffectType;
                            }

                            if (tipoConhecido == FFBEType.ET_NONE)
                            {
                                _log.Debug(LogPoint.VJoyEvents,
                                    $"[Condição] Bloco {condition.EffectBlockIndex} ainda sem tipo conhecido (aguardando PT_EFFREP). " +
                                    "Será tratado como Spring (posição) até lá.");
                            }

                            RecalculateConditionEffects();
                        }
                        else
                        {
                            _log.Debug(LogPoint.VJoyEvents, "[vJoy FFB] Condição no eixo Y ignorada (bridge só trata o eixo X/rotação).");
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
                        int magnitude = (int)periodicEffect.Magnitude;
                        int magnitudeAjustada = (int)Math.Round(magnitude * _ffbConfig.MagnitudeMultiplier);
                        int offset = (int)periodicEffect.Offset;

                        _log.Info(LogPoint.VJoyEvents,
                            $"[vJoy FFB] ---> EFEITO PERIÓDICO ATUALIZADO | Mag original: {magnitude} | " +
                            $"Mag ajustada: {magnitudeAjustada} | Offset: {offset}");
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
                        _log.Info(LogPoint.VJoyEvents, $"[vJoy FFB] ---> OPERAÇÃO DE MOTOR: {effectOp.EffectOp} | Bloco: {effectOp.EffectBlockIndex}");

                        bool isConditionBlock;
                        lock (_ffbLock)
                        {
                            isConditionBlock = _conditionEffects.TryGetValue(effectOp.EffectBlockIndex, out var state);
                            if (isConditionBlock)
                            {
                                if (effectOp.EffectOp == FFBOP.EFF_START || effectOp.EffectOp == FFBOP.EFF_SOLO)
                                {
                                    state!.Active = true;
                                }
                                else if (effectOp.EffectOp == FFBOP.EFF_STOP)
                                {
                                    state!.Active = false;
                                }
                            }
                        }

                        if (isConditionBlock && effectOp.EffectOp is FFBOP.EFF_START or FFBOP.EFF_SOLO)
                        {
                            _log.Info(LogPoint.VJoyEvents, $"[Condição] Bloco {effectOp.EffectBlockIndex} ATIVADO. Recalculando a cada atualização do eixo X.");
                            RecalculateConditionEffects();
                        }
                        else if (isConditionBlock && effectOp.EffectOp == FFBOP.EFF_STOP)
                        {
                            _log.Info(LogPoint.VJoyEvents, $"[Condição] Bloco {effectOp.EffectBlockIndex} DESATIVADO.");
                        }

                        // Se o jogo mandar parar (qualquer efeito), zera a força enviada ao STM32
                        if (effectOp.EffectOp == FFBOP.EFF_STOP)
                        {
                            _log.Info(LogPoint.VJoyEvents, "[Bridge -> STM32] COMANDO STOP! Enviando PWM: 0");
                            OnForceFeedbackReceived?.Invoke(0, 0); // Desliga o motor
                        }
                    }
                    else
                    {
                        _log.Warning(LogPoint.VJoyEvents, "[vJoy FFB] PT_EFOPREP recebido, mas Ffb_h_EffOp falhou ao decodificar.");
                    }
                    break;

                default:
                    // Tipo de pacote que a bridge ainda não trata (ex: Ramp, Custom, Envelope, Sample...).
                    _log.Warning(LogPoint.VJoyEvents, $"[vJoy FFB] Tipo de pacote NÃO TRATADO recebido: {packetType} (ptr=0x{packet.ToInt64():X})");
                    break;
            }
        }

        private ConditionEffectState GetOrCreateConditionState(byte effectBlockIndex)
        {
            if (!_conditionEffects.TryGetValue(effectBlockIndex, out var state))
            {
                state = new ConditionEffectState();
                _conditionEffects[effectBlockIndex] = state;
            }
            return state;
        }

        private static bool IsConditionEffectType(FFBEType type) =>
            type is FFBEType.ET_SPRNG or FFBEType.ET_DMPR or FFBEType.ET_INRT or FFBEType.ET_FRCTN;

        /// <summary>
        /// Atualiza posição normalizada, velocidade e aceleração do eixo X a partir de um
        /// novo valor bruto (0..32767) e do tempo decorrido desde a última amostra, então
        /// recalcula todos os efeitos de condição ativos.
        /// </summary>
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
                    if (dt > 0.0005) // evita picos por amostras "coladas" no tempo
                    {
                        double newVelocity = (normalized - _lastNormalizedPos) / dt;
                        _lastAcceleration = (newVelocity - _lastVelocity) / dt;
                        _lastVelocity = newVelocity;
                    }
                    // Se dt for baixo demais, reaproveita a última velocidade/aceleração conhecidas.
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

        private static double NormalizePosition(int axisValue) => ((axisValue - 16383.5) / 16383.5) * 10000.0;

        /// <summary>
        /// Soma a força de todos os efeitos de condição ATIVOS (Spring usa posição,
        /// Damper/Friction usam velocidade, Inertia usa aceleração) e envia o PWM resultante.
        /// </summary>
        private void RecalculateConditionEffects()
        {
            double normalizedPosition, velocity, acceleration;
            List<(FfbCondition condition, FFBEType type)>? activeSnapshot = null;

            lock (_ffbLock)
            {
                normalizedPosition = _lastNormalizedPos;
                velocity = _lastVelocity;
                acceleration = _lastAcceleration;

                foreach (var state in _conditionEffects.Values)
                {
                    if (!state.Active) continue;
                    (activeSnapshot ??= new List<(FfbCondition, FFBEType)>()).Add((state.Condition, state.EffectType));
                }
            }

            if (activeSnapshot == null || activeSnapshot.Count == 0) return;

            double totalForce = 0;
            foreach (var (condition, type) in activeSnapshot)
            {
                double metric = type switch
                {
                    FFBEType.ET_DMPR or FFBEType.ET_FRCTN => velocity * _ffbConfig.VelocityScale,
                    FFBEType.ET_INRT => acceleration * _ffbConfig.AccelerationScale,
                    // ET_SPRNG e qualquer bloco ainda sem PT_EFFREP (tipo desconhecido) usa posição.
                    _ => normalizedPosition
                };

                totalForce += CalculateConditionForce(condition, metric);
            }

            // Trava de segurança na escala do protocolo FFB (-10000..10000) antes do multiplicador global.
            totalForce = Math.Clamp(totalForce, -10000.0, 10000.0) * _ffbConfig.MagnitudeMultiplier;

            int pwm = Math.Clamp((int)Math.Round(Math.Abs(totalForce) * 255.0 / 10000.0), 0, 255);
            int direction = totalForce >= 0 ? 1 : 0;

            _log.Debug(LogPoint.VJoyEvents,
                $"[Condição] pos={normalizedPosition:F0} vel={velocity:F0} acc={acceleration:F0} " +
                $"efeitos_ativos={activeSnapshot.Count} força_total={totalForce:F0} pwm={pwm} dir={direction}");

            OnForceFeedbackReceived?.Invoke(pwm, direction);
        }

        /// <summary>
        /// Fórmula padrão do HID PID (USB Physical Interface Device) para efeitos de condição:
        ///   abaixo da zona morta: força = (métrica - (centro - deadBand)) * NegCoeff
        ///   acima da zona morta:  força = (métrica - (centro + deadBand)) * PosCoeff
        ///   dentro da zona morta: força = 0
        /// onde "métrica" é posição (Spring), velocidade (Damper/Friction) ou aceleração (Inertia).
        /// </summary>
        private static double CalculateConditionForce(FfbCondition condition, double metric)
        {
            double deadBandLow = condition.CenterPointOffset - condition.DeadBand;
            double deadBandHigh = condition.CenterPointOffset + condition.DeadBand;

            double force;
            if (metric < deadBandLow)
                force = (metric - deadBandLow) * condition.NegCoeff / 10000.0;
            else if (metric > deadBandHigh)
                force = (metric - deadBandHigh) * condition.PosCoeff / 10000.0;
            else
                force = 0.0;

            return force >= 0
                ? Math.Min(force, (double)condition.PosSatur)
                : Math.Max(force, -(double)condition.NegSatur);
        }
    }
}
