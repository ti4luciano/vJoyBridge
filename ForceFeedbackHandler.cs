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
            _log.Debug(LogPoint.VJoyEvents, $"[vJoy FFB] Pacote recebido | ptr=0x{packet.ToInt64():X} | Ffb_h_Type retorno={typeResult} | Tipo={packetType}");
        }

        public void ProcessDeviceControl(FFB_CTRL control, Action onStopAll, Action onReset)
        {
            _log.Info(LogPoint.VJoyEvents, $"[vJoy FFB] ---> CONTROLE DE DISPOSITIVO: {control}");

            if (control == FFB_CTRL.CTRL_STOPALL)
            {
                _log.Info(LogPoint.VJoyEvents, "[Bridge -> STM32] PARADA DE EMERGÊNCIA (STOP ALL)! Interrompendo efeitos -> PWM: 0");
                onStopAll?.Invoke();
            }
            else if (control == FFB_CTRL.CTRL_DEVRST)
            {
                _log.Info(LogPoint.VJoyEvents, "[Bridge -> STM32] RESET DE DISPOSITIVO! Limpando todos os efeitos -> PWM: 0, Gain: 255");
                onReset?.Invoke();
            }
        }

        public void ProcessDeviceGain(byte gain)
        {
            double percent = gain / 255.0 * 100.0;
            _log.Info(LogPoint.VJoyEvents, $"[vJoy FFB] ---> GANHO GLOBAL ALTERADO: {gain}/255 ({percent:F0}%)");
        }

        public void ProcessEffectReport(byte blockIndex, FFBEType effectType, uint direction)
        {
            _log.Info(LogPoint.VJoyEvents, $"[vJoy FFB] ---> NOVO EFEITO CONFIGURADO | Bloco: {blockIndex} | Tipo: {effectType} | Direção: {direction}");
        }

        public void ProcessConstantEffect(int magnitude)
        {
            int magnitudeAjustada = (int)Math.Round(magnitude * _config.MagnitudeMultiplier);
            _log.Info(LogPoint.VJoyEvents, $"[vJoy FFB] ---> EFEITO CONSTANTE ATUALIZADO | Mag original: {magnitude} | Multiplicador: {_config.MagnitudeMultiplier} | Mag ajustada: {magnitudeAjustada}");
        }

        public void ProcessConditionEffect(vJoy.FFB_EFF_COND condition, FFBEType effectType)
        {
            _log.Info(LogPoint.VJoyEvents,
                $"[vJoy FFB] ---> CONDIÇÃO RECEBIDA | Bloco: {condition.EffectBlockIndex} | Tipo: {effectType} | Eixo: {(condition.isY ? "Y" : "X")} | " +
                $"Centro: {condition.CenterPointOffset} | PosCoeff: {condition.PosCoeff} | NegCoeff: {condition.NegCoeff} | " +
                $"PosSat: {condition.PosSatur} | NegSat: {condition.NegSatur} | DeadBand: {condition.DeadBand}");
        }

        public void ProcessPeriodicEffect(byte blockIndex, FFBEType effectType, uint magnitude, int offset, uint phase, uint period)
        {
            _log.Info(LogPoint.VJoyEvents,
                $"[vJoy FFB] ---> EFEITO PERIÓDICO ATUALIZADO | Bloco: {blockIndex} | Tipo: {effectType} | Mag: {magnitude} | Offset: {offset} | Phase: {phase} | Period: {period}ms");
        }

        public void ProcessEffectOperation(FFBOP operation, byte blockIndex, Action onStart, Action onStop)
        {
            _log.Info(LogPoint.VJoyEvents, $"[vJoy FFB] ---> OPERAÇÃO DE EFEITO: {operation} | Bloco: {blockIndex}");

            if (operation is FFBOP.EFF_START or FFBOP.EFF_SOLO)
            {
                _log.Info(LogPoint.VJoyEvents, $"[vJoy FFB] Bloco {blockIndex} INICIADO/ATIVADO.");
                onStart?.Invoke();
            }
            else if (operation == FFBOP.EFF_STOP)
            {
                _log.Info(LogPoint.VJoyEvents, $"[vJoy FFB] Bloco {blockIndex} PARADO/DESATIVADO.");
                onStop?.Invoke();
            }
        }

        public void LogUnhandledPacket(FFBPType packetType, IntPtr packet)
        {
            _log.Warning(LogPoint.VJoyEvents, $"[vJoy FFB] Tipo de pacote NÃO TRATADO recebido: {packetType} (ptr=0x{packet.ToInt64():X})");
        }

        public void LogForceCalculation(double totalForce, int pwm, int direction, int activeEffectsCount, byte globalGain)
        {
            _log.Debug(LogPoint.VJoyEvents,
                $"[Bridge -> STM32] Efeitos Ativos: {activeEffectsCount} | Gain: {globalGain}/255 | Force Total: {totalForce:F0} | Output PWM: {pwm} | Dir: {direction}");
        }

        // ============================================================================
        // CÁLCULOS MATEMÁTICOS DE FORCE FEEDBACK
        // ============================================================================

        /// <summary>
        /// Calcula a força gerada por efeitos de Condição (Spring, Damper, Friction, Inertia) segundo o padrão HID PID.
        /// </summary>
        public double CalculateConditionForce(vJoy.FFB_EFF_COND condition, double metric)
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

        /// <summary>
        /// Calcula a força de onda de um efeito Periódico (Sine, Square, Triangle, Sawtooth Up/Down)
        /// com base no tempo decorrido desde a ativação do efeito.
        /// </summary>
        public double CalculatePeriodicForce(FFBEType effectType, uint magnitude, int offset, uint phase, uint period, long elapsedTimeMs)
        {
            if (period == 0) return offset;

            // Phase vem em centésimos de grau (0..36000)
            double phaseNorm = (phase % 36000) / 36000.0;
            double timeNorm = (((elapsedTimeMs % period) / (double)period) + phaseNorm) % 1.0;

            double wave = effectType switch
            {
                FFBEType.ET_SINE => Math.Sin(2.0 * Math.PI * timeNorm),
                FFBEType.ET_SQR => timeNorm < 0.5 ? 1.0 : -1.0,
                FFBEType.ET_TRI => 1.0 - 4.0 * Math.Abs(timeNorm - 0.5),
                FFBEType.ET_SAWT => 2.0 * timeNorm - 1.0,  // Sawtooth Up: -1 -> +1
                FFBEType.ET_SAWD => 1.0 - 2.0 * timeNorm,  // Sawtooth Down: +1 -> -1
                _ => 0.0
            };

            return offset + (magnitude * wave);
        }

        /// <summary>
        /// Aplica Ganho Global, Multiplicador da Config, Limite de Segurança (-10000..10000) e Converte para PWM (0..255) e Direção.
        /// </summary>
        public void CalculateFinalForce(double rawForce, byte globalGain, out int pwm, out int direction, out double finalForce)
        {
            double gainFactor = globalGain / 255.0;
            finalForce = Math.Clamp(rawForce * _config.MagnitudeMultiplier * gainFactor, -10000.0, 10000.0);

            pwm = Math.Clamp((int)Math.Round(Math.Abs(finalForce) * 255.0 / 10000.0), 0, 255);
            direction = finalForce >= 0 ? 1 : 0;
        }
    }
}