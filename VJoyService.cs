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

        private vJoyInterfaceWrap.vJoy? _joystick;

        // =========================================================================
        // --- P/Invoke Direto para a vJoyInterface.dll (Nativo C++) ---
        // Bypassa as limitações de FFB do wrapper intermediário vJoyInterfaceWrap.dll
        // =========================================================================

        private const uint ERROR_SUCCESS = 0;

        public bool Initialize(uint deviceId)
        {
            _joystick = new vJoyInterfaceWrap.vJoy();

            if (!_joystick.vJoyEnabled())
            {
                Console.WriteLine("[vJoy] Erro: Driver não encontrado ou DLL ausente.");
                return false;
            }

            VjdStat status = _joystick.GetVJDStatus(deviceId);
            if (status != VjdStat.VJD_STAT_FREE && status != VjdStat.VJD_STAT_OWN)
            {
                Console.WriteLine($"[vJoy] Erro: Dispositivo {deviceId} ocupado. Status: {status}");
                return false;
            }

            if (!_joystick.AcquireVJD(deviceId))
            {
                Console.WriteLine($"[vJoy] Erro ao adquirir o dispositivo {deviceId}.");
                return false;
            }

            Console.WriteLine($"[vJoy] Dispositivo {deviceId} adquirido com sucesso.");

            // Inicialização do FFB Real
            if (!_joystick.IsDeviceFfb(deviceId))
            {
                Console.WriteLine($"[vJoy] ATENÇÃO: Dispositivo {deviceId} não está configurado para suportar FFB no painel do vJoy.");
            }
            else
            {
                // Registra o callback usando a API do wrapper vJoyInterfaceWrap.
                // Esta versão do vJoy expõe os helpers Ffb_h_* e evita o EntryPointNotFoundException.
                _joystick.FfbRegisterGenCB(OnFfbDataReceived, null);
                Console.WriteLine("[vJoy] Canal FFB registrado via wrapper vJoyInterfaceWrap.");
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
            Console.WriteLine($"[vJoy] Dispositivo {deviceId} liberado.");
        }

        /// <summary>
        /// Callback de FFB interpretando os pacotes pelo wrapper oficial do vJoy.
        /// </summary>
        private void OnFfbDataReceived(IntPtr packet, object userData)
        {
            FFBPType packetType = FFBPType.PT_EFFREP;
            if (_joystick?.Ffb_h_Type(packet, ref packetType) != ERROR_SUCCESS)
            {
                return;
            }

            Console.WriteLine($"[vJoy FFB RAW] Comando recebido: Tipo {packetType}");

            if (packetType == FFBPType.PT_CTRLREP)
            {
                FFB_CTRL control = 0;
                if (_joystick?.Ffb_h_DevCtrl(packet, ref control) == ERROR_SUCCESS)
                {
                    Console.WriteLine($"[vJoy FFB] Controle de dispositivo recebido: {control}");
                }

                return;
            }

            if (packetType != FFBPType.PT_EFFREP)
            {
                return;
            }

            vJoyInterfaceWrap.vJoy.FFB_EFF_REPORT effectReport = default;
            if (_joystick?.Ffb_h_Eff_Report(packet, ref effectReport) != ERROR_SUCCESS)
            {
                return;
            }

            if (effectReport.EffectType == FFBEType.ET_CONST)
            {
                vJoyInterfaceWrap.vJoy.FFB_EFF_CONSTANT constantEffect = default;
                if (_joystick?.Ffb_h_Eff_Constant(packet, ref constantEffect) == ERROR_SUCCESS)
                {
                    int magnitude = constantEffect.Magnitude;
                    int pwm = Math.Abs(magnitude) * 255 / 10000;
                    int direction = magnitude >= 0 ? 1 : 0;

                    pwm = Math.Clamp(pwm, 0, 255);

                    OnForceFeedbackReceived?.Invoke(pwm, direction);

                    Console.WriteLine($"[vJoy FFB] CONST -> Magnitude: {magnitude} | PWM: {pwm} | Dir: {direction}");
                }
            }
            else
            {
                Console.WriteLine($"[vJoy FFB] EFFECT REPORT -> Efeito: {effectReport.EffectType}");
            }
        }
    }
}