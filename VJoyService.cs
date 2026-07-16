#nullable enable
using System;
using System.Runtime.InteropServices;
using vJoyInterfaceWrap;

namespace vJoyBridge
{
    /// <summary>
    /// Implementação universal de FFB que lê diretamente a memória do pacote DirectInput.
    /// Compatível com qualquer versão do vJoy (Padrão ou Brunner).
    /// </summary>
    public class VJoyService : IJoystickService
    {
        public event Action<int, int>? OnForceFeedbackReceived;

        private vJoy? _joystick;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FfbCbFunc(IntPtr packet, IntPtr userData);

        [DllImport("vJoyInterface.dll", EntryPoint = "FfbRegisterGenCB", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FfbRegisterGenCB(FfbCbFunc cb, IntPtr data);

        private FfbCbFunc? _ffbCallbackHolder;

        public bool Initialize(uint deviceId)
        {
            _joystick = new vJoy();

            if (!_joystick.vJoyEnabled())
            {
                Console.WriteLine("[vJoy] Erro: Driver não encontrado.");
                return false;
            }

            VjdStat status = _joystick.GetVJDStatus(deviceId);
            if (status != VjdStat.VJD_STAT_FREE && status != VjdStat.VJD_STAT_OWN)
            {
                Console.WriteLine($"[vJoy] Erro: Dispositivo {deviceId} ocupado.");
                return false;
            }

            if (!_joystick.AcquireVJD(deviceId))
            {
                Console.WriteLine($"[vJoy] Erro ao adquirir o dispositivo {deviceId}.");
                return false;
            }

            if (!_joystick.IsDeviceFfb(deviceId))
            {
                Console.WriteLine($"[vJoy] ATENÇÃO: Dispositivo {deviceId} sem suporte a FFB ativo.");
            }
            else
            {
                // Registra o callback na DLL nativa
                _ffbCallbackHolder = new FfbCbFunc(OnFfbDataReceived);
                FfbRegisterGenCB(_ffbCallbackHolder, IntPtr.Zero);
                Console.WriteLine($"[vJoy] Escuta de FFB ativa (Modo Universal de Memória).");
            }

            return true;
        }

        public void SetAxis(uint deviceId, HID_USAGES axis, int value)
        {
            _joystick?.SetAxis(Math.Clamp(value, 0, 32767), deviceId, axis);
        }

        public void Shutdown(uint deviceId)
        {
            _joystick?.RelinquishVJD(deviceId);
            Console.WriteLine($"[vJoy] Dispositivo {deviceId} liberado.");
        }

        /// <summary>
        /// Desempacota o bloco de dados DirectInput direto da memória.
        /// </summary>
        private void OnFfbDataReceived(IntPtr packet, IntPtr userData)
        {
            if (packet == IntPtr.Zero) return;

            try
            {
                // Estrutura FFB_DATA padrão:
                // Offset 0: Size (4 bytes)
                // Offset 4: CmdType (4 bytes)
                uint cmdType = (uint)Marshal.ReadInt32(packet, 4);

                if (cmdType == 2) // FFBPKT_CONST (Efeito de força constante enviado pelo jogo)
                {
                    // Estrutura FFB_EFF_CONSTANT:
                    // Offset 8: Effect Index (1 byte) + 3 bytes de padding para alinhamento de 32-bits
                    // Offset 12: Magnitude (4 bytes, signed int variando de -10000 a 10000)
                    int magnitude = Marshal.ReadInt32(packet, 12);

                    // Mapeamento para PWM (0-255) e Direção (0 ou 1)
                    int pwm = Math.Abs(magnitude) * 255 / 10000;
                    int direction = magnitude >= 0 ? 1 : 0;

                    pwm = Math.Clamp(pwm, 0, 255);

                    // Repassa o comando FFB limpo para o controlador enviar ao STM32
                    OnForceFeedbackReceived?.Invoke(pwm, direction);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[vJoy FFB] Falha na leitura de memória: {ex.Message}");
            }
        }
    }
}