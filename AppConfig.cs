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

        /// <summary>
        /// Faixa bruta de valores que chegam do encoder do eixo X (rotação do volante),
        /// remapeada internamente para a faixa lógica do vJoy (0..32767). Ajuste aqui
        /// quando trocar o encoder, sem precisar mexer em código.
        /// </summary>
        public AxisRangeConfig AxisX { get; set; } = new();
    }

    public class AxisRangeConfig
    {
        /// <summary>Menor valor bruto que o encoder produz.</summary>
        public int RawMin { get; set; } = 0;

        /// <summary>Maior valor bruto que o encoder produz (hoje 512 pelo hardware atual).</summary>
        public int RawMax { get; set; } = 512;
    }

    public class SerialConfig
    {
        /// <summary>Nome da porta COM utilizada para comunicação com o STM32.</summary>
        public string PortName { get; set; } = "COM6";

        /// <summary>Baud rate da comunicação serial.</summary>
        public int BaudRate { get; set; } = 115200;

        /// <summary>Comportamento de reconexão automática caso a porta caia/seja desconectada.</summary>
        public ReconnectConfig Reconnect { get; set; } = new();
    }

    public class ReconnectConfig
    {
        /// <summary>Liga/desliga a reconexão automática.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Número máximo de tentativas de reconexão. Use 0 para tentar indefinidamente
        /// até que a aplicação seja encerrada.
        /// </summary>
        public int MaxAttempts { get; set; } = 5;

        /// <summary>Intervalo em milissegundos entre cada tentativa de reconexão.</summary>
        public int DelayMs { get; set; } = 3000;
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
        /// Multiplicador aplicado à magnitude final (após somar todos os efeitos) antes de
        /// convertê-la em PWM. Útil quando o atuador/mecanismo do hardware precisa de mais
        /// (ou menos) força do que a magnitude original enviada pelo jogo. 1.0 = sem alteração.
        /// </summary>
        public double MagnitudeMultiplier { get; set; } = 1.0;

        /// <summary>
        /// Ajuste fino aplicado à velocidade calculada do eixo antes de entrar na fórmula de
        /// Damper/Friction. A velocidade não tem uma escala "natural" como a posição, então
        /// esse fator existe para calibrar a sensibilidade sem mexer em código.
        /// </summary>
        public double VelocityScale { get; set; } = 1.0;

        /// <summary>
        /// Ajuste fino aplicado à aceleração calculada do eixo antes de entrar na fórmula de
        /// Inertia. Mesmo raciocínio do VelocityScale.
        /// </summary>
        public double AccelerationScale { get; set; } = 1.0;
    }
}
