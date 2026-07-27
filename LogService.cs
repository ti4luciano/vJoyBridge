using System;
using System.IO;

namespace vJoyBridge
{
    /// <summary>
    /// Implementação de log em console/arquivo, controlada pelo config.json.
    /// Thread-safe: a bridge escreve a partir de múltiplas threads
    /// (leitura serial e callback nativo de FFB).
    /// </summary>
    public class LogService : ILogService
    {
        private readonly LoggingConfig _config;
        private readonly string _logFilePath;
        private readonly object _lock = new();

        public LogService(LoggingConfig config)
        {
            _config = config;

            _logFilePath = Path.IsPathRooted(config.LogFilePath)
                ? config.LogFilePath
                : Path.Combine(AppContext.BaseDirectory, config.LogFilePath);

            if (_config.Enabled && _config.LogToFile)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(_logFilePath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Log] Não foi possível preparar o arquivo de log: {ex.Message}");
                }
            }
        }

        public void Debug(LogPoint point, string message) => Log(LogLevel.Debug, point, message);
        public void Info(LogPoint point, string message) => Log(LogLevel.Info, point, message);
        public void Warning(LogPoint point, string message) => Log(LogLevel.Warning, point, message);
        public void Error(LogPoint point, string message) => Log(LogLevel.Error, point, message);

        public void Log(LogLevel level, LogPoint point, string message)
        {
            if (!_config.Enabled) return;
            if (level < _config.Level) return;

            // Pontos de alta frequência só logam se explicitamente habilitados.
            // "General" (start/stop/erros de infraestrutura) sempre passa, desde que Enabled/Level ok.
            if (point == LogPoint.SerialToVJoy && !_config.Points.SerialToVJoy) return;
            if (point == LogPoint.VJoyEvents && !_config.Points.VJoyEvents) return;

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{point}] {message}";

            lock (_lock)
            {
                if (_config.LogToConsole)
                {
                    Console.WriteLine(line);
                }

                if (_config.LogToFile)
                {
                    try
                    {
                        File.AppendAllText(_logFilePath, line + Environment.NewLine);
                    }
                    catch
                    {
                        // Evita que falha de disco derrube a aplicação.
                    }
                }
            }
        }
    }
}
