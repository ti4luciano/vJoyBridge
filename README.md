# vJoyBridge

Bridge em C#/.NET que conecta um volante DIY baseado em STM32 (via porta serial) a um dispositivo virtual **vJoy**, incluindo suporte a **Force Feedback (FFB)** com motor DC.

## Visão geral

O projeto é composto por duas partes:

1. **Firmware (`firmware.ino`)** — roda em uma placa STM32 (core Arduino_STM32 / USBComposite) e lê encoder, potenciômetros e botões do volante.
2. **Bridge (aplicação .NET)** — roda no PC, lê os dados vindos da serial, envia para o dispositivo virtual vJoy e traduz os pacotes de Force Feedback recebidos do jogo em comandos de PWM/direção para o motor.

```
[Volante STM32] <--Serial (115200 bps)--> [vJoyBridge (.NET)] <---> [vJoy Driver] <---> [Jogo/Simulador]
```

### Modos do firmware

O firmware suporta dois modos, decididos em um handshake de 3 segundos no boot:

- **Modo Serial**: se o firmware receber o caractere `H` pela serial nos primeiros 3s, ele entra em modo texto e passa a enviar `X:<val> Y:<val> Z:<val> B:<bitmask>` a cada 100ms, além de aceitar comandos `F:<pwm>,<dir>` para acionar o motor de força.
- **Modo USB HID nativo**: se nenhum handshake ocorrer, o firmware desliga o CDC serial e sobe como joystick USB HID nativo (sem FFB, sem depender do bridge).

O `vJoyBridge` sempre opera assumindo o **modo Serial**.

## Arquitetura da aplicação (.NET)

| Arquivo | Responsabilidade |
|---|---|
| `Program.cs` | Ponto de entrada; monta as dependências e inicia o bridge |
| `AppConfig.cs` | Modelos de configuração (fortemente tipados) |
| `ConfigService.cs` | Carrega/cria o `config.json` ao lado do executável |
| `BridgeController.cs` | Orquestra o fluxo Serial ↔ vJoy |
| `SerialService.cs` / `ISerialService.cs` | Comunicação serial com reconexão automática |
| `VJoyService.cs` / `IJoystickService.cs` | Integração com o driver vJoy, incluindo o motor de FFB |
| `ForceFeedbackHandler.cs` | Interpreta pacotes FFB (constante, condição, periódico, controle, ganho) |
| `LogService.cs` / `ILogService.cs` | Log em console/arquivo, configurável por nível e por ponto de origem |
| `config.json` | Configuração padrão gerada/consumida em runtime |

### Fluxo de dados

1. `SerialService` abre a porta configurada, faz handshake (`H`) e lê linhas continuamente em uma thread dedicada.
2. Cada linha recebida (`X:.. Y:.. Z:..`) é parseada por `BridgeController` e aplicada aos eixos correspondentes via `IJoystickService.SetAxis`.
3. `VJoyService` registra um callback nativo de FFB (`FfbRegisterGenCB`) e traduz os pacotes do driver vJoy em força (PWM 0–255 + direção 0/1).
4. O `BridgeController` escuta o evento `OnForceFeedbackReceived` e envia `F:<pwm>,<direção>` de volta pela serial para o STM32 mover o motor.

### Force Feedback

O `VJoyService` implementa um pipeline relativamente completo de FFB:

- **Efeitos de condição** (Spring, Damper, Inertia, Friction), calculados a partir de posição, velocidade e aceleração do eixo X (derivadas numericamente a cada amostra).
- **Efeitos periódicos** (Sine, Square, Triangle, Sawtooth Up/Down), calculados por tempo decorrido desde a ativação do bloco de efeito, via um timer de 10ms.
- **Efeito constante**, repassado diretamente como PWM/direção.
- **Controle de dispositivo** (`STOP ALL`, `DEVICE RESET`) e **ganho global** (0–255), aplicados sobre a força final antes do envio.

Todos os cálculos de força usam a escala nativa do vJoy (±10000) e são convertidos para PWM de 0–255 antes de seguir para a serial.

## Configuração (`config.json`)

Gerado automaticamente na primeira execução, ao lado do executável, caso não exista.

```json
{
  "VJoy": {
    "DeviceId": 1,
    "AxisX": { "RawMin": 0, "RawMax": 512 }
  },
  "Serial": {
    "PortName": "COM6",
    "BaudRate": 115200,
    "Reconnect": {
      "Enabled": true,
      "MaxAttempts": 5,
      "DelayMs": 3000
    }
  },
  "Logging": {
    "Enabled": false,
    "Level": "Debug",
    "LogToConsole": true,
    "LogToFile": true,
    "LogFilePath": "vjoybridge.log",
    "Points": {
      "SerialToVJoy": false,
      "VJoyEvents": true
    }
  },
  "ForceFeedback": {
    "MagnitudeMultiplier": 1.0,
    "VelocityScale": 1.0,
    "AccelerationScale": 1.0
  }
}
```

| Seção | Campo | Descrição |
|---|---|---|
| `VJoy` | `DeviceId` | ID do dispositivo vJoy a ser adquirido (1–16) |
| `VJoy.AxisX` | `RawMin` / `RawMax` | Faixa bruta do encoder para normalização do eixo X |
| `Serial` | `PortName` / `BaudRate` | Porta e velocidade da conexão com o STM32 |
| `Serial.Reconnect` | `Enabled` / `MaxAttempts` / `DelayMs` | Reconexão automática em caso de queda da porta |
| `Logging` | `Enabled` / `Level` | Liga/desliga o log e o nível mínimo (`Debug`, `Info`, `Warning`, `Error`) |
| `Logging` | `LogToConsole` / `LogToFile` / `LogFilePath` | Destinos do log |
| `Logging.Points` | `SerialToVJoy` / `VJoyEvents` | Liga/desliga pontos de log de alta frequência individualmente |
| `ForceFeedback` | `MagnitudeMultiplier` / `VelocityScale` / `AccelerationScale` | Ajuste fino da resposta de força |

## Requisitos

- Windows x64
- [vJoy](https://sourceforge.net/projects/vjoystick/) instalado e um dispositivo configurado (mínimo: eixo X, 8 botões)
- .NET 9.0 (ou versão self-contained publicada, ver abaixo)
- Placa STM32 com o firmware (`firmware.ino`) gravado, core [Arduino_STM32](https://github.com/rogerclarkmelbourne/Arduino_STM32) com biblioteca `USBComposite`

### Hardware do firmware

| Função | Pino |
|---|---|
| Encoder (volante) A/B | PA1 / PA0 |
| Potenciômetro eixo Y | PB1 |
| Potenciômetro eixo Z | PB0 |
| LED de status | PC13 |
| Motor de força (IN1/IN2) | PA3 / PA2 |
| Botões 1–8 | PB5–PB12 |

## Build e publicação

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Gera um executável único, self-contained, para Windows x64.

> As DLLs nativas do vJoy (`vJoyInterfaceWrap.dll` e `vJoyInterface.dll`) devem estar em `lib/` na raiz do projeto — são referenciadas no `.csproj` e copiadas para a saída do build.

## Uso

1. Grave `firmware.ino` no STM32.
2. Instale e configure o driver vJoy (dispositivo 1, com eixo X e 8 botões).
3. Execute o `vJoyBridge.exe`. Na primeira execução, um `config.json` padrão é criado ao lado do executável.
4. Ajuste `Serial.PortName` conforme a porta COM do seu STM32.
5. Rode novamente. O programa fica ativo até pressionar **ESC**.

## Licença / Autoria

Projeto pessoal — **Luciano Alves**.
