namespace vJoyBridge
{
    /// <summary>
    /// Raiz do arquivo config.json, lido/gravado ao lado do executável.
    /// </summary>
    public class AppConfig
    {
        public VJoyConfig VJoy { get; set; } = new();
        public SerialConfig Serial { get; set; } = new();
        public LoggingConfig Logging { get; set; } = new();
        public ForceFeedbackConfig ForceFeedback { get; set; } = new();
    }

    public class VJoyConfig
    {
        /// <summary>ID do dispositivo vJoy a ser adquirido (padrão 1).</summary>
        public uint DeviceId { get; set; } = 1;
    }

    public class SerialConfig
    {
        /// <summary>Nome da porta COM utilizada para comunicação com o STM32.</summary>
        public string PortName { get; set; } = "COM6";

        /// <summary>Baud rate da comunicação serial.</summary>
        public int BaudRate { get; set; } = 115200;
    }

    public class LoggingConfig
    {
        /// <summary>Chave geral: liga/desliga todo o sistema de log.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Nível mínimo de mensagens a serem registradas.</summary>
        public LogLevel Level { get; set; } = LogLevel.Info;

        /// <summary>Se verdadeiro, escreve as mensagens também no console.</summary>
        public bool LogToConsole { get; set; } = true;

        /// <summary>Se verdadeiro, escreve as mensagens em arquivo.</summary>
        public bool LogToFile { get; set; } = true;

        /// <summary>Caminho do arquivo de log. Relativo à pasta do executável, se não for absoluto.</summary>
        public string LogFilePath { get; set; } = "vjoybridge.log";

        /// <summary>Habilita/desabilita cada ponto de log individualmente (evita ruído).</summary>
        public LogPointsConfig Points { get; set; } = new();
    }

    public class LogPointsConfig
    {
        /// <summary>
        /// Ponto 1: mensagens recebidas via Serial e traduzidas para os eixos do vJoy
        /// (BridgeController.HandleSerialMessage). Tende a ter MUITA ocorrência.
        /// </summary>
        public bool SerialToVJoy { get; set; } = false;

        /// <summary>
        /// Ponto 2: eventos de Force Feedback recebidos do vJoy/jogo
        /// (VJoyService.OnFfbDataReceived). Também tende a ter MUITA ocorrência.
        /// </summary>
        public bool VJoyEvents { get; set; } = true;
    }

    public class ForceFeedbackConfig
    {
        /// <summary>
        /// Multiplicador aplicado à magnitude do efeito de FFB antes de convertê-la em PWM.
        /// Útil quando o atuador/mecanismo do hardware precisa de mais (ou menos) força
        /// do que a magnitude original enviada pelo jogo. 1.0 = sem alteração.
        /// </summary>
        public double MagnitudeMultiplier { get; set; } = 1.0;
    }
}
