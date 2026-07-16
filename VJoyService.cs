#nullable enable
using System;
using System.Runtime.InteropServices;
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

        private vJoy? _joystick;

        // =========================================================================
        // --- P/Invoke Direto para a vJoyInterface.dll (Nativo C++) ---
        // Bypassa as limitações de FFB do wrapper intermediário vJoyInterfaceWrap.dll
        // =========================================================================

        private const uint ERROR_SUCCESS = 0;

        // Assinatura de callback correspondente ao driver vJoy nativo
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FfbCbFunc(IntPtr packet, IntPtr userData);

        [DllImport("vJoyInterface.dll", EntryPoint = "FfbRegisterGenCB", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FfbRegisterGenCB(FfbCbFunc cb, IntPtr data);

        [DllImport("vJoyInterface.dll", EntryPoint = "FfbGetCommandType", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FfbGetCommandType(IntPtr packet, ref uint type);

        [DllImport("vJoyInterface.dll", EntryPoint = "FfbGetConstForce", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FfbGetConstForce(IntPtr packet, ref int magnitude);

        [DllImport("vJoyInterface.dll", EntryPoint = "FfbGetEffectType", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FfbGetEffectType(IntPtr packet, ref uint effectType);

        // Mantém a referência do delegate ativa na memória para evitar Garbage Collection do ponteiro
        private FfbCbFunc? _ffbCallbackHolder;

        public bool Initialize(uint deviceId)
        {
            _joystick = new vJoy();

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
                // Registra o callback nativo diretamente na DLL de C++
                _ffbCallbackHolder = new FfbCbFunc(OnFfbDataReceived);
                FfbRegisterGenCB(_ffbCallbackHolder, IntPtr.Zero);
                Console.WriteLine($"[vJoy] Canal FFB registrado via P/Invoke na vJoyInterface.dll.");
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
        /// Callback de FFB em baixa latência executado diretamente pela DLL nativa do driver.
        /// </summary>
        private void OnFfbDataReceived(IntPtr packet, IntPtr userData)
        {
            uint cmdType = 0;
            if (FfbGetCommandType(packet, ref cmdType) != ERROR_SUCCESS) return;

            Console.WriteLine($"[vJoy FFB RAW] Comando recebido: Tipo {cmdType}");

            // Controle de dispositivo (Play, Stop, Reset)
            if (cmdType == 6) // FFBPKT_DEVCTRL
            {
                Console.WriteLine("[vJoy FFB] Comando de controle de dispositivo recebido (Ex: Reset/Ativar).");
                return;
            }

            // Força Constante (Principal efeito gerado pela simulação física do jogo)
            if (cmdType == 2) // FFBPKT_CONST
            {
                int magnitude = 0;
                // No C++ nativo, retornar ERROR_SUCCESS (0) indica sucesso
                if (FfbGetConstForce(packet, ref magnitude) == ERROR_SUCCESS)
                {
                    // DirectInput magnitude varia de -10000 (esquerda) a 10000 (direita)
                    // Traduzimos para PWM (0 a 255) e Direção (0 ou 1) para a ponte H
                    int pwm = Math.Abs(magnitude) * 255 / 10000;
                    int direction = magnitude >= 0 ? 1 : 0;

                    pwm = Math.Clamp(pwm, 0, 255);

                    // Dispara o evento repassando ao controlador
                    OnForceFeedbackReceived?.Invoke(pwm, direction);

                    Console.WriteLine($"[vJoy FFB] CONST -> Magnitude: {magnitude} | PWM: {pwm} | Dir: {direction}");
                }
            }
            else if (cmdType == 1) // FFBPKT_EFFREP (Fricção, Mola, etc)
            {
                uint effectType = 0;
                if (FfbGetEffectType(packet, ref effectType) == ERROR_SUCCESS)
                {
                    Console.WriteLine($"[vJoy FFB] EFFECT REPORT -> Efeito: {effectType} (Mola/Amortecimento/Fricção)");
                }
            }
        }
    }
}