using System;
using System.Threading;

namespace vJoyBridge
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Iniciando vJoy - STM32 Bridge ===");

            // 0. Carrega (ou cria, se ausente) o config.json ao lado do executável
            AppConfig config = ConfigService.Load();

            // 1. Instanciação das Dependências (Manual Dependency Injection)
            ILogService logService = new LogService(config.Logging);
            ISerialService serialService = new SerialService(logService);
            IJoystickService vJoyService = new VJoyService(logService, config.ForceFeedback);

            // 2. Injeta as dependências no controlador
            BridgeController bridge = new BridgeController(serialService, vJoyService, logService, config.VJoy.DeviceId);

            try
            {
                // 3. Inicia o sistema usando porta/baud rate do config.json
                bridge.Start(config.Serial.PortName, config.Serial.BaudRate);

                Console.WriteLine("\nSistema rodando. Pressione [ESC] para sair.\n");

                // Mantém o console aberto até pressionar ESC
                while (Console.ReadKey(true).Key != ConsoleKey.Escape)
                {
                    Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                logService.Error(LogPoint.General, $"[Fatal] Erro não tratado: {ex.Message}");
            }
            finally
            {
                // 4. Garante que os recursos serão liberados corretamente
                bridge.Stop();
            }
        }
    }
}
