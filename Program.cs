using System;
using System.Threading;

namespace vJoyBridge
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Iniciando vJoy - STM32 Bridge ===");

            // 1. Instanciação das Dependências (Manual Dependency Injection)
            ISerialService serialService = new SerialService();
            IJoystickService vJoyService = new VJoyService();

            // 2. Injeta as dependências no controlador
            BridgeController bridge = new BridgeController(serialService, vJoyService, deviceId: 1);

            try
            {
                // 3. Inicia o sistema
                bridge.Start("COM4", 115200);

                Console.WriteLine("\nSistema rodando. Pressione [ESC] para sair.\n");
                
                // Mantém o console aberto até pressionar ESC
                while (Console.ReadKey(true).Key != ConsoleKey.Escape)
                {
                    Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fatal] Erro não tratado: {ex.Message}");
            }
            finally
            {
                // 4. Garante que os recursos serão liberados corretamente
                bridge.Stop();
            }
        }
    }
}