namespace vJoyBridge
{
    /// <summary>Nível de severidade da mensagem de log.</summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }

    /// <summary>
    /// Ponto de origem do log dentro do pipeline da bridge.
    /// Permite ligar/desligar cada ponto de forma independente, já que
    /// SerialToVJoy e VJoyEvents têm alta frequência de ocorrência.
    /// </summary>
    public enum LogPoint
    {
        /// <summary>Mensagens gerais (start/stop/config/erros de infraestrutura). Sempre respeitam Enabled/Level.</summary>
        General,

        /// <summary>Comando chegando via Serial e sendo traduzido/enviado para o vJoy.</summary>
        SerialToVJoy,

        /// <summary>Eventos de Force Feedback chegando do vJoy/jogo.</summary>
        VJoyEvents
    }

    public interface ILogService
    {
        void Log(LogLevel level, LogPoint point, string message);

        void Debug(LogPoint point, string message);
        void Info(LogPoint point, string message);
        void Warning(LogPoint point, string message);
        void Error(LogPoint point, string message);
    }
}
