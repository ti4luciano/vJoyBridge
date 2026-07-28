// ============================================================================
// Program.cs
// ============================================================================
using System;
using System.Threading;

namespace vJoyBridge
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== vJoy - STM32 Bridge ===");

            AppConfig config = ConfigService.Load();

            ILogService logService = new LogService(config.Logging);
            ISerialService serialService = new SerialService(logService, config.Serial.Reconnect);
            IJoystickService vJoyService = new VJoyService(logService, config.ForceFeedback, config.VJoy.AxisX);
            BridgeController bridge = new BridgeController(serialService, vJoyService, logService, config.VJoy.DeviceId);

            try
            {
                bridge.Start(config.Serial.PortName, config.Serial.BaudRate);
                Console.WriteLine("\nSystem running. Press [ESC] to exit.\n");

                while (Console.ReadKey(true).Key != ConsoleKey.Escape)
                {
                    Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                logService.Error(LogPoint.General, $"[Fatal] Unhandled error: {ex.Message}");
            }
            finally
            {
                bridge.Stop();
            }
        }
    }
}