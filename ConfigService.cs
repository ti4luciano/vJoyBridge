using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace vJoyBridge
{
    /// <summary>
    /// Responsável por carregar (ou criar, se ausente) o arquivo config.json
    /// que fica ao lado do executável.
    /// </summary>
    public static class ConfigService
    {
        private const string ConfigFileName = "config.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

        public static AppConfig Load()
        {
            string path = ConfigPath;

            if (!File.Exists(path))
            {
                Console.WriteLine($"[Config] Arquivo '{ConfigFileName}' não encontrado. Criando um com valores padrão em: {path}");
                var defaultConfig = new AppConfig();
                Save(defaultConfig);
                return defaultConfig;
            }

            try
            {
                string json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);

                if (config == null)
                {
                    Console.WriteLine("[Config] Arquivo config.json vazio ou inválido. Usando valores padrão.");
                    return new AppConfig();
                }

                return config;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Erro ao ler '{ConfigFileName}': {ex.Message}. Usando valores padrão.");
                return new AppConfig();
            }
        }

        public static void Save(AppConfig config)
        {
            try
            {
                string json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Erro ao salvar '{ConfigFileName}': {ex.Message}");
            }
        }
    }
}
