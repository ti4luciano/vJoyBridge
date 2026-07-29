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
    }
}