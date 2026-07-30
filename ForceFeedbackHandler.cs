// ============================================================================
// ForceFeedbackHandler.cs
// ============================================================================
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using vJoyInterfaceWrap;
using FfbCondition = vJoyInterfaceWrap.vJoy.FFB_EFF_COND;

namespace vJoyBridge
{
    /// <summary>
    /// Motor de Force Feedback. Único ponto de tratamento dos eventos FFB do vJoy:
    /// faz o parsing de todos os pacotes nativos (Ffb_h_*), mantém o estado de cada
    /// bloco de efeito, calcula a força resultante (Condição/Periódico/Constante/Ramp,
    /// Envelope, Duração, Ganho) e expõe o resultado já traduzido em PWM/direção via
    /// <see cref="OnForceFeedbackReceived"/>.
    ///
    /// VJoyService não contém nenhuma lógica de FFB - é só um adaptador do driver vJoy
    /// que repassa o ponteiro do pacote nativo e as amostras de eixo para cá.
    /// </summary>
    public class ForceFeedbackHandler
    {
        public event Action<int, int>? OnForceFeedbackReceived;

        private const uint ERROR_SUCCESS = 0;

        // Valor sentinela para "duração infinita" de um efeito (PT_EFFREP.Duration == 0 ou
        // 0xFFFF), conforme a especificação USB PID. Efeitos com duração infinita tocam até
        // serem explicitamente parados via PT_EFOPREP (EFF_STOP) ou CTRL_STOPALL/CTRL_DEVRST.
        private const uint InfiniteDuration = 0xFFFF;

        private readonly ILogService _log;
        private readonly ForceFeedbackConfig _ffbConfig;
        private readonly AxisRangeConfig _axisConfig;

        private class ConditionEffectState
        {
            public FfbCondition Condition;
            public FFBEType EffectType = FFBEType.ET_NONE;
            public bool Active;

            // Duração do efeito em ms, reportada via PT_EFFREP. InfiniteDuration (ou 0, antes do
            // primeiro EFFREP) significa que o efeito toca indefinidamente até ser parado.
            public uint Duration = InfiniteDuration;

            // Envelope opcional de attack/fade (PT_ENVREP), aplicado a efeitos Constante, Ramp e
            // Periódico, conforme o spec USB PID. Efeitos de Condição são excluídos por definição.
            public bool HasEnvelope;
            public uint AttackLevel;
            public uint FadeLevel;
            public uint AttackTime;
            public uint FadeTime;

            // Efeito Constante (PT_CONSTREP): magnitude com sinal, -10000..10000.
            public int ConstantMagnitude;

            // Efeito Ramp (PT_RAMPREP): interpolação linear de Start a End ao longo de Duration.
            public int RampStart;
            public int RampEnd;

            // Parâmetros de efeitos periódicos (square/sine/triangle/sawtooth), populados via PT_PRIDREP.
            public uint PeriodicMagnitude;
            public int PeriodicOffset;
            public uint PeriodicPhase;
            public uint PeriodicPeriod;

            // Timestamp (ticks do Stopwatch) da última transição para Active, usado como origem do
            // tempo para playback de forma de onda periódica, rampas e envelopes.
            public long ActivationTimestamp;
        }

        // Snapshot dos parâmetros de um efeito ativo, obtido com _ffbLock preso, para que o
        // cálculo de força seguinte possa rodar sem lock.
        private readonly struct EffectSnapshot
        {
            public FFBEType Type { get; init; }
            public FfbCondition Condition { get; init; }
            public uint Duration { get; init; }
            public bool HasEnvelope { get; init; }
            public uint AttackLevel { get; init; }
            public uint FadeLevel { get; init; }
            public uint AttackTime { get; init; }
            public uint FadeTime { get; init; }
            public int ConstantMagnitude { get; init; }
            public int RampStart { get; init; }
            public int RampEnd { get; init; }
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

        // Ganho global de FFB (0-255) reportado via PT_GAINREP. Escala toda força despachada,
        // já que o dispositivo aplica isso sobre o que qualquer efeito individual pedir.
        // Padrão 255 (100%), que é também o que um reset de dispositivo (CTRL_DEVRST) restaura.
        private volatile byte _globalGain = 255;

        // Marca se o último valor despachado foi diferente de zero, para garantir que, ao sair
        // da zona de endstop ou ao último efeito desativar, seja enviado um "F:0,0" final em vez
        // de simplesmente parar de mandar mensagens e deixar o motor preso na última força.
        private bool _forceCurrentlyNonZero;

        // Efeitos periódicos, Ramp e Constante (com envelope) são baseados em tempo e precisam
        // continuar produzindo força mesmo quando o eixo não se move e nenhum pacote FFB novo
        // chega, então recalculamos em um timer em vez de depender só de eventos de pacote/eixo.
        // Intervalo configurável via ForceFeedbackConfig.RecalculationIntervalMs (extraído da
        // referência de projetos DIY de wheel FFB que rodam o cálculo a 500Hz/2ms - bem mais fino
        // que os 10ms fixos usados anteriormente aqui, o que deixa efeitos periódicos e condição
        // visivelmente mais suaves).
        private System.Threading.Timer? _recalculationTimer;

        public ForceFeedbackHandler(ILogService log, ForceFeedbackConfig ffbConfig, AxisRangeConfig axisConfig)
        {
            _log = log;
            _ffbConfig = ffbConfig;
            _axisConfig = axisConfig;
        }

        /// <summary>Inicia o timer de recálculo periódico. Chamado por VJoyService após o registro do callback nativo de FFB.</summary>
        public void Start()
        {
            int intervalMs = Math.Max(1, _ffbConfig.RecalculationIntervalMs);
            _recalculationTimer = new System.Threading.Timer(_ => RecalculateEffects(), null, intervalMs, intervalMs);
        }

        /// <summary>Para o timer de recálculo periódico. Chamado por VJoyService no Shutdown.</summary>
        public void Stop()
        {
            _recalculationTimer?.Dispose();
            _recalculationTimer = null;
        }

        /// <summary>
        /// Recebe uma nova amostra bruta do eixo X, atualiza posição/velocidade/aceleração
        /// normalizadas (usadas pelos efeitos de Condição) e recalcula a força imediatamente.
        /// </summary>
        public void UpdateAxisPosition(int rawAxisValue)
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

            RecalculateEffects();
        }

        /// <summary>
        /// Ponto de entrada único para qualquer pacote FFB nativo recebido do vJoy. Faz o parsing
        /// (Ffb_h_*), atualiza o estado do bloco de efeito correspondente e despacha a força quando
        /// aplicável. <paramref name="joystick"/> é passado pelo VJoyService a cada chamada, já que
        /// quem possui o handle do dispositivo é o adaptador, não o motor de FFB.
        /// </summary>
        public void HandlePacket(vJoyInterfaceWrap.vJoy joystick, IntPtr packet)
        {
            FFBPType packetType = FFBPType.PT_EFFREP;
            uint typeResult = joystick.Ffb_h_Type(packet, ref packetType);

            _log.Debug(LogPoint.VJoyEvents, $"[FFB Packet] Ptr: 0x{packet.ToInt64():X} | Result: {typeResult} | Type: {packetType}");

            if (typeResult != ERROR_SUCCESS) return;

            switch (packetType)
            {
                case FFBPType.PT_CTRLREP:
                    FFB_CTRL control = 0;
                    if (joystick.Ffb_h_DevCtrl(packet, ref control) == ERROR_SUCCESS)
                    {
                        HandleDeviceControl(control);
                    }
                    break;

                case FFBPType.PT_EFFREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_REPORT effectReport = default;
                    if (joystick.Ffb_h_Eff_Report(packet, ref effectReport) == ERROR_SUCCESS)
                    {
                        lock (_ffbLock)
                        {
                            var state = GetOrCreateConditionState(effectReport.EffectBlockIndex);
                            state.EffectType = effectReport.EffectType;
                            state.Duration = effectReport.Duration == 0 ? InfiniteDuration : (uint)effectReport.Duration;
                        }
                    }
                    break;

                case FFBPType.PT_ENVREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_ENVLP envelope = default;
                    if (joystick.Ffb_h_Eff_Envlp(packet, ref envelope) == ERROR_SUCCESS)
                    {
                        lock (_ffbLock)
                        {
                            var state = GetOrCreateConditionState(envelope.EffectBlockIndex);
                            state.HasEnvelope = true;
                            state.AttackLevel = (uint)envelope.AttackLevel;
                            state.FadeLevel = (uint)envelope.FadeLevel;
                            state.AttackTime = (uint)envelope.AttackTime;
                            state.FadeTime = (uint)envelope.FadeTime;
                        }
                        _log.Info(LogPoint.VJoyEvents, $"[FFB Envelope] Block: {envelope.EffectBlockIndex} | Attack: {envelope.AttackLevel}/{envelope.AttackTime}ms | Fade: {envelope.FadeLevel}/{envelope.FadeTime}ms");
                    }
                    break;

                case FFBPType.PT_CONSTREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_CONSTANT constantEffect = default;
                    if (joystick.Ffb_h_Eff_Constant(packet, ref constantEffect) == ERROR_SUCCESS)
                    {
                        lock (_ffbLock)
                        {
                            GetOrCreateConditionState(constantEffect.EffectBlockIndex).ConstantMagnitude = constantEffect.Magnitude;
                        }
                        _log.Info(LogPoint.VJoyEvents, $"[FFB Constant] Magnitude: {constantEffect.Magnitude}");
                        RecalculateEffects();
                    }
                    break;

                case FFBPType.PT_RAMPREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_RAMP rampEffect = default;
                    if (joystick.Ffb_h_Eff_Ramp(packet, ref rampEffect) == ERROR_SUCCESS)
                    {
                        lock (_ffbLock)
                        {
                            var state = GetOrCreateConditionState(rampEffect.EffectBlockIndex);
                            state.RampStart = rampEffect.Start;
                            state.RampEnd = rampEffect.End;
                        }
                        _log.Info(LogPoint.VJoyEvents, $"[FFB Ramp] Block: {rampEffect.EffectBlockIndex} | Start: {rampEffect.Start} | End: {rampEffect.End}");
                        RecalculateEffects();
                    }
                    break;

                case FFBPType.PT_CONDREP:
                    FfbCondition condition = default;
                    if (joystick.Ffb_h_Eff_Cond(packet, ref condition) == ERROR_SUCCESS && !condition.isY)
                    {
                        lock (_ffbLock)
                        {
                            GetOrCreateConditionState(condition.EffectBlockIndex).Condition = condition;
                        }
                        RecalculateEffects();
                    }
                    break;

                case FFBPType.PT_EFOPREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_OP effectOp = default;
                    if (joystick.Ffb_h_EffOp(packet, ref effectOp) == ERROR_SUCCESS)
                    {
                        lock (_ffbLock)
                        {
                            if (_conditionEffects.TryGetValue(effectOp.EffectBlockIndex, out var state))
                            {
                                bool wasActive = state.Active;
                                state.Active = effectOp.EffectOp is FFBOP.EFF_START or FFBOP.EFF_SOLO;

                                // Reseta a origem do tempo da forma de onda toda vez que o efeito
                                // (re)inicia, para que efeitos periódicos, ramps e envelopes
                                // reiniciem do zero (fase 0 / início do attack / t=0 da rampa).
                                if (state.Active && !wasActive)
                                {
                                    state.ActivationTimestamp = Stopwatch.GetTimestamp();
                                }
                            }
                        }

                        _log.Info(LogPoint.VJoyEvents, $"[FFB Operation] Op: {effectOp.EffectOp} | Block: {effectOp.EffectBlockIndex}");
                        if (effectOp.EffectOp == FFBOP.EFF_STOP)
                        {
                            _log.Info(LogPoint.VJoyEvents, $"[FFB Operation] STOP effect block {effectOp.EffectBlockIndex} -> PWM: 0");
                            DispatchFfb(0, 0);
                        }
                    }
                    break;

                case FFBPType.PT_PRIDREP:
                    vJoyInterfaceWrap.vJoy.FFB_EFF_PERIOD periodicEffect = default;
                    if (joystick.Ffb_h_Eff_Period(packet, ref periodicEffect) == ERROR_SUCCESS)
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

                        _log.Info(LogPoint.VJoyEvents, $"[FFB Periodic] Block: {periodicEffect.EffectBlockIndex} | Type: {periodicType} | Magnitude: {periodicEffect.Magnitude} | Offset: {periodicEffect.Offset} | Phase: {periodicEffect.Phase} | Period: {periodicEffect.Period}");
                        RecalculateEffects();
                    }
                    break;

                case FFBPType.PT_NEWEFREP:
                    uint newBlockIndex = 0;
                    if (joystick.Ffb_h_EBI(packet, ref newBlockIndex) == ERROR_SUCCESS)
                    {
                        // O dispositivo pode reutilizar um índice de bloco previamente liberado.
                        // Limpa qualquer estado em cache (Active antigo, condição, ramp, envelope
                        // ou parâmetros periódicos) para que não vaze para o novo efeito que está
                        // prestes a ser configurado neste bloco.
                        lock (_ffbLock)
                        {
                            _conditionEffects.Remove((byte)newBlockIndex);
                        }
                        _log.Info(LogPoint.VJoyEvents, $"[FFB Block] New effect block allocated: {newBlockIndex}");
                    }
                    break;

                case FFBPType.PT_BLKFRREP:
                    uint freedBlockIndex = 0;
                    if (joystick.Ffb_h_EBI(packet, ref freedBlockIndex) == ERROR_SUCCESS)
                    {
                        // Para e esquece este bloco imediatamente. Se ficasse no dicionário
                        // ainda marcado Active, continuaria contribuindo força em
                        // RecalculateEffects() para sempre, mesmo depois do jogo liberá-lo -
                        // isso é o que produzia o comportamento "preso girando" observado em teste.
                        lock (_ffbLock)
                        {
                            _conditionEffects.Remove((byte)freedBlockIndex);
                        }
                        _log.Info(LogPoint.VJoyEvents, $"[FFB Block] Effect block freed: {freedBlockIndex} -> clearing cached state");
                        RecalculateEffects();
                    }
                    break;

                case FFBPType.PT_GAINREP:
                    byte gain = 255;
                    if (joystick.Ffb_h_DevGain(packet, ref gain) == ERROR_SUCCESS)
                    {
                        _globalGain = gain;
                        double percent = gain / 255.0 * 100.0;
                        _log.Info(LogPoint.VJoyEvents, $"[FFB Gain] Global gain set to {gain}/255 ({percent:F0}%)");

                        // Reaplica imediatamente ao efeito atualmente rodando, em vez de esperar
                        // a próxima amostra de eixo ou pacote FFB para pegar o novo ganho.
                        RecalculateEffects();
                    }
                    break;

                default:
                    // PT_CSTMREP / PT_SMPLREP / PT_SETCREP (Custom Force, com tabela de amostras
                    // baixada via download) e PT_BLKLDREP ficam fora de escopo intencionalmente:
                    // praticamente nenhum volante/simulador de consumo os utiliza, e implementar
                    // playback de forma de onda customizada exigiria armazenar e reproduzir um
                    // buffer de amostras arbitrário do jogo. Seguem apenas logados.
                    _log.Warning(LogPoint.VJoyEvents, $"[FFB Unhandled] Unrecognized packet type: {packetType} (0x{packet.ToInt64():X})");
                    break;
            }
        }

        private void HandleDeviceControl(FFB_CTRL control)
        {
            _log.Info(LogPoint.VJoyEvents, $"[FFB Control] Device Command: {control}");

            if (control == FFB_CTRL.CTRL_STOPALL)
            {
                // Zerar a força imediatamente não basta: sem desativar os blocos de efeito,
                // o próximo tick do timer periódico (efeitos periódicos/ramp/constante ainda
                // "Active") recalcula e reaplica força, fazendo o STOP ALL parecer ignorado.
                lock (_ffbLock)
                {
                    foreach (var state in _conditionEffects.Values)
                        state.Active = false;
                }
                _log.Info(LogPoint.VJoyEvents, "[FFB Control] STOP ALL triggered -> PWM: 0, efeitos desativados");
                DispatchFfb(0, 0);
            }
            else if (control == FFB_CTRL.CTRL_DEVRST)
            {
                lock (_ffbLock)
                {
                    _conditionEffects.Clear();
                }
                _globalGain = 255;
                _log.Info(LogPoint.VJoyEvents, "[FFB Control] RESET triggered -> PWM: 0, Gain: 255");
                DispatchFfb(0, 0);
            }
        }

        private void DispatchFfb(int pwm, int direction)
        {
            byte gain = _globalGain;
            int scaledPwm = gain == 255 ? pwm : Math.Clamp((int)Math.Round(pwm * (gain / 255.0)), 0, 255);

            OnForceFeedbackReceived?.Invoke(scaledPwm, direction);
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

        private double NormalizePosition(int rawValue)
        {
            int range = _axisConfig.RawMax - _axisConfig.RawMin;
            if (range <= 0) range = 1;

            int clamped = Math.Clamp(rawValue, _axisConfig.RawMin, _axisConfig.RawMax);
            return (((double)(clamped - _axisConfig.RawMin) / range) * 2.0 - 1.0) * 10000.0;
        }

        private void RecalculateEffects()
        {
            long now = Stopwatch.GetTimestamp();
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

                    // Efeitos com duração finita se auto-encerram quando o tempo decorrido excede
                    // Duration, conforme o spec USB PID (o host não é obrigado a mandar EFF_STOP).
                    if (state.Duration != InfiniteDuration && state.Duration != 0)
                    {
                        double elapsedSinceStart = (now - state.ActivationTimestamp) * 1000.0 / Stopwatch.Frequency;
                        if (elapsedSinceStart > state.Duration)
                        {
                            state.Active = false;
                            continue;
                        }
                    }

                    (activeEffects ??= new List<EffectSnapshot>()).Add(new EffectSnapshot
                    {
                        Type = state.EffectType,
                        Condition = state.Condition,
                        Duration = state.Duration,
                        HasEnvelope = state.HasEnvelope,
                        AttackLevel = state.AttackLevel,
                        FadeLevel = state.FadeLevel,
                        AttackTime = state.AttackTime,
                        FadeTime = state.FadeTime,
                        ConstantMagnitude = state.ConstantMagnitude,
                        RampStart = state.RampStart,
                        RampEnd = state.RampEnd,
                        PeriodicMagnitude = state.PeriodicMagnitude,
                        PeriodicOffset = state.PeriodicOffset,
                        PeriodicPhase = state.PeriodicPhase,
                        PeriodicPeriod = state.PeriodicPeriod,
                        ActivationTimestamp = state.ActivationTimestamp
                    });
                }
            }

            double gameForce = 0;
            if (activeEffects != null)
            {
                foreach (var effect in activeEffects)
                {
                    double elapsedMs = (now - effect.ActivationTimestamp) * 1000.0 / Stopwatch.Frequency;

                    if (IsPeriodicEffectType(effect.Type))
                    {
                        double shapedMagnitude = ApplyEnvelope(effect.PeriodicMagnitude, effect, elapsedMs);
                        uint clampedMagnitude = (uint)Math.Clamp(Math.Round(shapedMagnitude), 0.0, 10000.0);
                        gameForce += CalculatePeriodicForce(effect.Type, clampedMagnitude, effect.PeriodicOffset,
                            effect.PeriodicPhase, effect.PeriodicPeriod, effect.ActivationTimestamp);
                        continue;
                    }

                    if (effect.Type == FFBEType.ET_CONST)
                    {
                        gameForce += ApplyEnvelope(effect.ConstantMagnitude, effect, elapsedMs);
                        continue;
                    }

                    if (effect.Type == FFBEType.ET_RAMP)
                    {
                        double t = (effect.Duration == InfiniteDuration || effect.Duration == 0)
                            ? 0.0
                            : Math.Clamp(elapsedMs / effect.Duration, 0.0, 1.0);
                        double ramped = effect.RampStart + (effect.RampEnd - effect.RampStart) * t;
                        gameForce += ApplyEnvelope(ramped, effect, elapsedMs);
                        continue;
                    }

                    if (IsConditionEffectType(effect.Type))
                    {
                        double metric = effect.Type switch
                        {
                            FFBEType.ET_DMPR or FFBEType.ET_FRCTN => velocity * _ffbConfig.VelocityScale,
                            FFBEType.ET_INRT => acceleration * _ffbConfig.AccelerationScale,
                            _ => normalizedPos
                        };

                        gameForce += CalculateConditionForce(effect.Condition, metric);
                    }
                }
            }

            gameForce = Math.Clamp(gameForce, -10000.0, 10000.0) * _ffbConfig.MagnitudeMultiplier;

            // A mola de fim de curso é independente do jogo: soma-se ao que o jogo pediu, em vez
            // de substituir. Continua funcionando mesmo se o jogo não mandar nenhum efeito perto
            // das pontas do curso (o cenário relatado nos testes).
            double endstopForce = CalculateEndstopForce(normalizedPos);

            bool hasWork = activeEffects != null || endstopForce != 0.0;
            if (!hasWork)
            {
                // Nada a fazer agora. Mas se o último tick tinha força não-nula (ex: acabou de
                // sair da zona de endstop, ou o último efeito do jogo acabou de desativar),
                // manda um "F:0,0" final em vez de simplesmente parar de escrever - senão o
                // motor ficaria preso na última força aplicada.
                if (_forceCurrentlyNonZero)
                {
                    _forceCurrentlyNonZero = false;
                    DispatchFfb(0, 0);
                }
                return;
            }

            double totalForce = Math.Clamp(gameForce + endstopForce, -10000.0, 10000.0);

            int pwm = Math.Clamp((int)Math.Round(Math.Abs(totalForce) * 255.0 / 10000.0), 0, 255);
            int direction = totalForce >= 0 ? 1 : 0;

            _forceCurrentlyNonZero = pwm != 0;
            DispatchFfb(pwm, direction);
        }

        // Mola de fim de curso (endstop): cresce progressivamente (t²) a partir da borda da zona
        // de margem até a intensidade máxima configurada, bem no limite físico do eixo. Fora da
        // zona de margem, retorna 0 e não interfere em nada. O sinal sempre empurra de volta para
        // o centro, qualquer que seja o lado em que o eixo esteja.
        private double CalculateEndstopForce(double normalizedPos)
        {
            var cfg = _ffbConfig.Endstop;
            if (!cfg.Enabled || cfg.Strength <= 0) return 0.0;

            double marginRaw = 10000.0 * Math.Clamp(cfg.MarginPercent, 0.0, 1.0);
            if (marginRaw <= 0) return 0.0;

            double threshold = 10000.0 - marginRaw;
            double absPos = Math.Abs(normalizedPos);

            if (absPos <= threshold) return 0.0;

            double t = Math.Clamp((absPos - threshold) / marginRaw, 0.0, 1.0);
            double magnitude = Math.Clamp(cfg.Strength, 0.0, 10000.0) * t * t;

            return normalizedPos >= 0 ? -magnitude : magnitude;
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

        // Aplica o envelope de attack/fade (PT_ENVREP) a uma magnitude com sinal, seguindo o
        // spec USB PID: rampa linear de AttackLevel até |magnitude| durante os primeiros
        // AttackTime ms, sustenta |magnitude|, depois rampa linear até FadeLevel nos últimos
        // FadeTime ms antes de Duration. Efeitos com duração infinita não sofrem fade (não há
        // "últimos ms" definidos), apenas attack. AttackLevel/FadeLevel são magnitudes
        // absolutas (0-10000); o sinal da magnitude original é preservado.
        private static double ApplyEnvelope(double magnitude, EffectSnapshot effect, double elapsedMs)
        {
            if (!effect.HasEnvelope) return magnitude;

            double sign = magnitude >= 0 ? 1.0 : -1.0;
            double absMag = Math.Abs(magnitude);
            double level = absMag;

            bool hasFiniteDuration = effect.Duration != InfiniteDuration && effect.Duration != 0;

            if (effect.AttackTime > 0 && elapsedMs < effect.AttackTime)
            {
                double t = elapsedMs / effect.AttackTime;
                level = effect.AttackLevel + (absMag - effect.AttackLevel) * t;
            }
            else if (hasFiniteDuration && effect.FadeTime > 0 && elapsedMs > effect.Duration - effect.FadeTime)
            {
                double fadeElapsed = elapsedMs - (effect.Duration - effect.FadeTime);
                double t = Math.Clamp(fadeElapsed / effect.FadeTime, 0.0, 1.0);
                level = absMag + (effect.FadeLevel - absMag) * t;
            }

            return sign * Math.Clamp(level, 0.0, 10000.0);
        }
    }
}