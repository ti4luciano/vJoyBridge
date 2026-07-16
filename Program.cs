using System;
using System.IO.Ports;
using System.Threading;

namespace vJoyBridge
{
    class Program
    {
        static vJoyInterfaceWrap.vJoy? joystick = null;
        static uint id = 1;
        static bool isRunning = true;
        static SerialPort serialPort;

        static void Main(string[] args)
        {
            joystick = new vJoyInterfaceWrap.vJoy();

            if (!joystick.vJoyEnabled())
            {
                Console.WriteLine("Erro: vJoy não encontrado ou DLL ausente.");
                return;
            }

            VjdStat status = joystick.GetVJDStatus(id);
            if (status == VjdStat.VJD_STAT_FREE)
            {
                joystick.AcquireVJD(id);
                Console.WriteLine("vJoy adquirido com sucesso.");
            }
            else
            {
                Console.WriteLine($"Erro: vJoy ID {id} não está livre (Status: {status}).");
                return;
            }

            try
            {
                serialPort = new SerialPort("COM4", 115200)
                {
                    DtrEnable = true,  // Essencial para Arduinos/STM32 via USB CDC
                    ReadTimeout = 50,  // Timeout baixo para não travar o loop
                    NewLine = "\n"
                };
                serialPort.Open();

                // Limpa o lixo inicial da porta
                serialPort.DiscardInBuffer();
                Console.WriteLine("Conectado na COM4. Iniciando leitura de alta performance...");

                // Inicia a thread dedicada para ler a serial
                Thread readThread = new Thread(ReadSerialLoop)
                {
                    IsBackground = true
                };
                readThread.Start();

                Console.WriteLine("Pressione [ESC] para sair.");
                while (Console.ReadKey(true).Key != ConsoleKey.Escape)
                {
                    // Aguarda o usuário pedir para sair
                    Thread.Sleep(100);
                }

                isRunning = false;
                readThread.Join(); // Aguarda a thread de leitura encerrar graciosamente
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Falha fatal: {ex.Message}");
            }
            finally
            {
                if (serialPort != null && serialPort.IsOpen)
                    serialPort.Close();

                if (joystick != null)
                    joystick.RelinquishVJD(id);
                
                Console.WriteLine("Encerrado.");
            }
        }

        // --- Thread dedicada para leitura (Substitui o DataReceived) ---
        private static void ReadSerialLoop()
        {
            if (serialPort == null) return;

            while (isRunning)
            {
                try
                {
                    // Se não há dados, respira um pouco para não fritar a CPU
                    if (serialPort.BytesToRead == 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    // Lê a linha. Se der timeout, a exceção é capturada abaixo
                    string linha = serialPort.ReadLine(); 

                    // Ignora linhas vazias ou lixo inicial
                    if (string.IsNullOrWhiteSpace(linha)) continue;

                    linha = linha.Trim();

                    if (linha.StartsWith("X:"))
                    {
                        string valStr = linha.Substring(2);
                        if (int.TryParse(valStr, out int pos))
                        {
                            // Mostra no console para confirmar que está vivo!
                            Console.WriteLine($"[STM32] {pos}");

                            // O STM32 envia int16_t (-32768 a 32767)
                            // O vJoy espera (0 a 32767)
                            // Regra de três: (pos + 32768) / 2
                            int vJoyValue = (pos + 32768) / 2;
                            
                            if (joystick != null)
                            {
                                joystick.SetAxis(Math.Clamp(vJoyValue, 0, 32767), id, HID_USAGES.HID_USAGE_X);
                            }
                        }
                    }
                    else
                    {
                        // Loga dados estranhos para ajudar no debug
                        Console.WriteLine($"[IGNORADO] {linha}");
                    }
                }
                catch (TimeoutException)
                {
                    // Timeout esperado (50ms). Continua o loop.
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erro na leitura: {ex.Message}");
                    Thread.Sleep(100); // Pausa breve antes de tentar de novo
                }
            }
        }
    }
}