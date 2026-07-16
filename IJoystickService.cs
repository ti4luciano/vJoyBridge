using vJoyInterfaceWrap;

namespace vJoyBridge
{
    /// <summary>
    /// Contrato para o serviço de controle do Joystick Virtual.
    /// Isola a aplicação da dependência direta da biblioteca do vJoy.
    /// </summary>
    public interface IJoystickService
    {
        /// <summary>Inicializa e adquire o controle do dispositivo virtual.</summary>
        /// <param name="deviceId">ID do dispositivo vJoy (padrão 1).</param>
        /// <returns>Verdadeiro se adquirido com sucesso.</returns>
        bool Initialize(uint deviceId);

        /// <summary>Define o valor de um eixo específico.</summary>
        /// <param name="deviceId">ID do dispositivo vJoy.</param>
        /// <param name="axis">Eixo a ser modificado (ex: HID_USAGE_X).</param>
        /// <param name="value">Valor entre 0 e 32767.</param>
        void SetAxis(uint deviceId, HID_USAGES axis, int value);

        /// <summary>Libera o dispositivo virtual e encerra a conexão.</summary>
        void Shutdown(uint deviceId);
    }
}