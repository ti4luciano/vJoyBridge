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
| `VJoyService.cs` / `IJoystickService.cs` | Adaptador fino do driver vJoy: aquisição/liberação do dispositivo, eixos, botões e registro do callback nativo de FFB. **Não contém lógica de FFB** — apenas repassa pacote/eixo para `ForceFeedbackHandler` |
| `ForceFeedbackHandler.cs` | Dono exclusivo de todo o tratamento de eventos de Force Feedback: parsing dos pacotes nativos (`Ffb_h_*`), estado de cada bloco de efeito, timer de recálculo, e toda a matemática (condição/periódico/constante/ramp/envelope/ganho/duração) |
| `LogService.cs` / `ILogService.cs` | Log em console/arquivo, configurável por nível e por ponto de origem |
| `config.json` | Configuração padrão gerada/consumida em runtime |

### Fluxo de dados

1. `SerialService` abre a porta configurada, faz handshake (`H`) e lê linhas continuamente em uma thread dedicada.
2. Cada linha recebida (`X:.. Y:.. Z:..`) é parseada por `BridgeController` e aplicada aos eixos correspondentes via `IJoystickService.SetAxis`.
3. `VJoyService` (adaptador) grava o eixo no dispositivo vJoy e repassa a amostra bruta para `ForceFeedbackHandler.UpdateAxisPosition`, que deriva posição/velocidade/aceleração normalizadas.
4. O driver vJoy invoca o callback nativo de FFB registrado por `VJoyService`, que apenas encaminha o ponteiro do pacote para `ForceFeedbackHandler.HandlePacket` — todo o parsing e cálculo acontecem lá.
5. `ForceFeedbackHandler` expõe o resultado (PWM 0–255 + direção) via `OnForceFeedbackReceived`; `BridgeController` escuta esse evento e envia `F:<pwm>,<direção>` pela serial para o STM32 mover o motor.

Essa divisão existe para manter uma única responsabilidade por classe: `VJoyService` sabe falar com o driver vJoy (I/O), `ForceFeedbackHandler` sabe interpretar o protocolo FFB e transformar efeitos em força (domínio/cálculo). Antes, boa parte dessa segunda responsabilidade vivia dentro do `VJoyService`, o que misturava as duas coisas numa classe só.

### Force Feedback

O `VJoyService` implementa o pipeline de FFB cobrindo todos os tipos de efeito paramétricos do spec USB PID:

- **Efeitos de condição** (Spring, Damper, Inertia, Friction), calculados a partir de posição, velocidade e aceleração do eixo X (derivadas numericamente a cada amostra).
- **Efeitos periódicos** (Sine, Square, Triangle, Sawtooth Up/Down), calculados por tempo decorrido desde a ativação do bloco de efeito, via um timer de 10ms.
- **Efeito constante**, com magnitude com sinal.
- **Efeito Ramp**, interpolado linearmente entre `Start` e `End` ao longo da `Duration` do efeito.
- **Envelope de attack/fade** (`PT_ENVREP`), aplicado sobre Constante, Ramp e Periódico — sobe linearmente de `AttackLevel` até a magnitude plena nos primeiros `AttackTime` ms, sustenta, e desce até `FadeLevel` nos últimos `FadeTime` ms antes da `Duration` (efeitos de duração infinita só sofrem attack). Efeitos de condição são excluídos do envelope, como define o spec.
- **Duração de efeito**, respeitada: um efeito com `Duration` finita se autodesativa quando o tempo decorrido a ultrapassa, sem depender de o jogo mandar `EFF_STOP`.
- **Controle de dispositivo**: `DEVICE RESET` limpa todos os blocos de efeito e restaura o ganho; `STOP ALL` desativa todos os blocos ativos (além de zerar a força na hora), evitando que um efeito periódico/ramp/constante "ressuscite" a força no próximo tick do timer.
- **Ganho global** (0–255, `PT_GAINREP`), aplicado sobre a força final antes do envio, com recálculo imediato ao ser alterado.

Todos os cálculos de força usam a escala nativa do vJoy (±10000) e são convertidos para PWM de 0–255 antes de seguir para a serial. Todos os tipos de efeito passam pela mesma soma central em `RecalculateConditionEffects`, garantindo que `MagnitudeMultiplier` seja aplicado de forma consistente independente do tipo.

**Fora de escopo (intencional)**: efeitos de **Custom Force** (`PT_CSTMREP`/`PT_SMPLREP`/`PT_SETCREP`), que descrevem uma forma de onda arbitrária via tabela de amostras baixada do jogo. Praticamente nenhum simulador de consumo os utiliza; pacotes desse tipo são apenas logados (`LogUnhandledPacket`), não descartados silenciosamente.

> Os nomes dos métodos/structs do wrapper (`Ffb_h_Eff_Ramp`, `Ffb_h_Eff_Envlp`, `FFB_EFF_REPORT.Duration`) seguem a API pública padrão do SDK do vJoy. Como o `vJoyInterfaceWrap.dll` usado neste projeto não foi disponibilizado para inspeção, vale conferir esses nomes contra a versão do `.dll` em `lib/` antes de compilar.

### Melhorias incorporadas (referência: [ranenbg/Arduino-FFB-wheel](https://github.com/ranenbg/Arduino-FFB-wheel))

Esse projeto é uma referência séria em wheels FFB DIY (baseado no BRWheel). Duas práticas de lá foram trazidas para este projeto:

- **Taxa de recálculo de FFB mais alta.** A referência calcula a força a 500Hz (2ms). O `RecalculationIntervalMs` em `config.json` (padrão `2`) substitui os 10ms fixos que este bridge usava antes — efeitos periódicos e de condição ficam mais suaves.
- **Watchdog de segurança do motor no firmware.** Um motor DC preso ao volante nas mãos do usuário não pode ficar travado em força alta se o processo `vJoyBridge.exe` no PC crashar ou for encerrado sem fechar a porta corretamente (a porta COM pode continuar "aberta" no Windows mesmo com o processo pendurado). `firmware.ino` agora zera o motor (`FFB_WATCHDOG_MS`, padrão 500ms) se nenhum comando `F:` chegar dentro desse intervalo. Enquanto houver efeito de FFB ativo, o bridge já manda `F:` continuamente a cada 2–10ms, então esse fluxo funciona como heartbeat natural — o watchdog só dispara quando o host realmente para de responder.

Uma outra ideia da referência **não** foi incorporada, por decisão consciente:
- **PWM em frequência mais alta (8kHz+) no driver de motor** — reduz ruído audível e aumenta a resolução perto de zero. Não implementei porque depende de configurar registradores de timer específicos do STM32 usado (`analogWrite` hoje roda na frequência padrão do core Arduino_STM32), e eu não tenho como validar isso sem compilar contra a placa real.

### Mola de fim de curso (endstop)

Força sintética, gerada inteiramente pelo bridge — **não depende de nenhum pacote FFB do jogo**. Existe para dar resistência perto dos limites físicos do eixo mesmo quando o jogo não manda nenhum efeito ali (o cenário relatado em testes: sem resistência nenhuma nas extremidades).

Funcionamento: fora de uma "zona de margem" perto de cada ponta do curso, força 0 (não interfere em nada). Dentro da zona, a força cresce progressivamente (`t²`, não linear) da borda da zona até a intensidade máxima configurada, bem no limite físico — sempre empurrando de volta para o centro. Ela se soma à força que o jogo estiver pedindo (spring/damper/etc.), em vez de substituí-la.

Configurável em `ForceFeedback.Endstop` no `config.json`:

```json
"Endstop": {
  "Enabled": true,
  "MarginPercent": 0.08,
  "Strength": 6000
}
```

| Campo | Descrição |
|---|---|
| `Enabled` | Liga/desliga a mola de fim de curso |
| `MarginPercent` | Fração do curso total (0.0–1.0), a partir de CADA ponta, onde a mola passa a atuar. `0.08` = últimos 8% de cada lado |
| `Strength` | Magnitude máxima (escala vJoy: 0–10000) aplicada bem no limite físico do eixo |

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