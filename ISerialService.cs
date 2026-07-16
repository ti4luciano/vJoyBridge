using System;

namespace vJoyBridge
{
    /// <summary>
    /// Contrato para comunicação serial.
    /// Define eventos para recebimento e métodos para envio, abstraindo a porta COM.
    /// </summary>
    public interface ISerialService
    {
        /// <summary>Evento disparado sempre que uma linha completa de texto é recebida.</summary>
        event Action<string> OnMessageReceived;

        /// <summary>Abre a conexão com a porta serial e inicia a thread de leitura.</summary>
        void Connect(string portName, int baudRate);

        /// <summary>Envia uma mensagem de texto para o dispositivo conectado.</summary>
        void SendMessage(string message);

        /// <summary>Encerra a conexão e finaliza a thread de leitura.</summary>
        void Disconnect();
    }
}