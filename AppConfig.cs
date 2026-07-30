// ============================================================================
// AppConfig.cs
// ============================================================================
namespace vJoyBridge
{
    public class AppConfig
    {
        public VJoyConfig VJoy { get; set; } = new();
        public SerialConfig Serial { get; set; } = new();
        public LoggingConfig Logging { get; set; } = new();
        public ForceFeedbackConfig ForceFeedback { get; set; } = new();
    }

    public class VJoyConfig
    {
        public uint DeviceId { get; set; } = 1;
        public AxisRangeConfig AxisX { get; set; } = new();
    }

    public class AxisRangeConfig
    {
        public int RawMin { get; set; } = 0;
        public int RawMax { get; set; } = 512;
    }

    public class SerialConfig
    {
        public string PortName { get; set; } = "COM6";
        public int BaudRate { get; set; } = 115200;
        public ReconnectConfig Reconnect { get; set; } = new();
    }

    public class ReconnectConfig
    {
        public bool Enabled { get; set; } = true;
        public int MaxAttempts { get; set; } = 5;
        public int DelayMs { get; set; } = 3000;
    }

    public class LoggingConfig
    {
        public bool Enabled { get; set; } = false;
        public LogLevel Level { get; set; } = LogLevel.Info;
        public bool LogToConsole { get; set; } = true;
        public bool LogToFile { get; set; } = true;
        public string LogFilePath { get; set; } = "vjoybridge.log";
        public LogPointsConfig Points { get; set; } = new();
    }

    public class LogPointsConfig
    {
        public bool SerialToVJoy { get; set; } = false;
        public bool VJoyEvents { get; set; } = true;
    }

    public class ForceFeedbackConfig
    {
        public double MagnitudeMultiplier { get; set; } = 1.0;
        public double VelocityScale { get; set; } = 1.0;
        public double AccelerationScale { get; set; } = 1.0;

        // Intervalo do timer que recalcula efeitos baseados em tempo (Periódico/Ramp/Constante
        // com envelope). Extraído de referências de wheels FFB DIY que rodam esse cálculo a
        // 500Hz (2ms); valores mais altos economizam CPU mas deixam a forma de onda mais "em
        // degraus", especialmente perceptível em efeitos periódicos de frequência alta.
        public int RecalculationIntervalMs { get; set; } = 2;

        public EndstopConfig Endstop { get; set; } = new();
    }

    /// <summary>
    /// Mola de fim de curso (endstop): força sintética, independente de qualquer efeito FFB
    /// enviado pelo jogo, que empurra o eixo de volta ao centro quando ele se aproxima dos
    /// limites físicos (RawMin/RawMax do VJoyConfig.AxisX). Protege a mecânica em jogos que não
    /// aplicam nenhuma resistência perto do fim de curso.
    /// </summary>
    public class EndstopConfig
    {
        public bool Enabled { get; set; } = true;

        // Fração do curso total (0.0-1.0), a partir de CADA ponta, onde a mola passa a atuar.
        // 0.08 = os últimos 8% de cada lado. Fora dessa zona, força 0 - não interfere em nada.
        public double MarginPercent { get; set; } = 0.08;

        // Magnitude máxima (escala vJoy: 0-10000) aplicada bem no limite físico do eixo.
        // Cresce progressivamente (não linear) da borda da zona até aqui.
        public double Strength { get; set; } = 6000;
    }
}