#nullable enable
using System;
using vJoyInterfaceWrap;

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

                        // Se o jogo mandar parar tudo ou resetar, enviamos Força 0 para o Arduino
                        if (control == FFB_CTRL.CTRL_STOPALL || control == FFB_CTRL.CTRL_DEVRST)
                        {
                            _log.Info(LogPoint.VJoyEvents, "[Bridge -> STM32] PARADA DE EMERGÊNCIA/RESET! Enviando PWM: 0");
                            OnForceFeedbackReceived?.Invoke(0, 0); // 0 PWM
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
                        _log.Info(LogPoint.VJoyEvents, $"[vJoy FFB] ---> NOVO EFEITO CONFIGURADO | Tipo: {effectReport.EffectType} | Direção: {effectReport.Direction}");
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
                        _log.Info(LogPoint.VJoyEvents, $"[vJoy FFB] ---> OPERAÇÃO DE MOTOR: {effectOp.EffectOp}");

                        // Aqui nós verificamos se o comando é de STOP
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
                    // Tipo de pacote que a bridge ainda não trata — é aqui que provavelmente
                    // aparecem os eventos "não interpretados corretamente".
                    _log.Warning(LogPoint.VJoyEvents, $"[vJoy FFB] Tipo de pacote NÃO TRATADO recebido: {packetType} (ptr=0x{packet.ToInt64():X})");
                    break;
            }
        }
    }
}
