using System;
using vJoyInterfaceWrap;

namespace vJoyBridge
{
    /// <summary>
    /// Implementação concreta do serviço do vJoy usando o Wrapper nativo.
    /// </summary>
    public class VJoyService : IJoystickService
    {
        private vJoy? _joystick;

        public bool Initialize(uint deviceId)
        {
            _joystick = new vJoy();

            if (!_joystick.vJoyEnabled())
            {
                Console.WriteLine("[vJoy] Erro: Driver não encontrado ou DLL ausente.");
                return false;
            }

            VjdStat status = _joystick.GetVJDStatus(deviceId);
            if (status == VjdStat.VJD_STAT_FREE)
            {
                bool success = _joystick.AcquireVJD(deviceId);
                if (success) Console.WriteLine($"[vJoy] Dispositivo {deviceId} adquirido com sucesso.");
                return success;
            }

            Console.WriteLine($"[vJoy] Erro: Dispositivo {deviceId} não está livre. Status: {status}");
            return false;
        }

        public void SetAxis(uint deviceId, HID_USAGES axis, int value)
        {
            // O Clamp garante que o valor nunca ultrapasse o limite do driver (0 a 32767)
            int safeValue = Math.Clamp(value, 0, 32767);
            _joystick?.SetAxis(safeValue, deviceId, axis);
        }

        public void Shutdown(uint deviceId)
        {
            _joystick?.RelinquishVJD(deviceId);
            Console.WriteLine($"[vJoy] Dispositivo {deviceId} liberado.");
        }
    }
}