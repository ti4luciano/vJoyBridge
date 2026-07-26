//Usar a configuração de placa STM32F1xx -> STM32F103C6/fake
/*
 * Firmware FFB - Joystick USB HID
 * MCU: STM32F103C6
 * IDE: Arduino IDE (STM32duino)
 *
 * Este firmware usa a biblioteca compartilhada FFBCommon (mesma
 * usada no firmware Serial) para leitura do encoder e dos
 * potenciometros. A lógica especifica deste firmware (USB HID
 * Joystick) permanece aqui.
 *
 * Melhoria em relação à versão original: o encoder agora é lido
 * por interrupção (via FFBCommon), assim como no firmware Serial,
 * em vez de polling dentro do loop(). Antes, qualquer bloqueio no
 * loop (ex.: aguardar 10ms para enviar o USB) podia deixar passar
 * transições rápidas do encoder; agora nenhuma transição é perdida.
 *
 * Requer a biblioteca FFBCommon instalada (Sketch > Include Library
 * > Add .ZIP Library... - ver README).
 */

#include <USBComposite.h>
#include <FFBCommon.h>

#define PIN_ENC_CLK PA0
#define PIN_ENC_DT  PA1
#define PIN_POT_Y   PB1
#define PIN_POT_Z   PB0
#define PIN_LED     PC13

#define LIMITE_MIN 0
#define LIMITE_MAX 512

// ====================================================================
// OBJETOS COMPARTILHADOS (FFBCommon)
// ====================================================================
QuadratureEncoder encoder(PIN_ENC_CLK, PIN_ENC_DT, LIMITE_MIN, LIMITE_MAX);
AnalogAxis axisY(PIN_POT_Y);
AnalogAxis axisZ(PIN_POT_Z);
StatusLed  statusLed(PIN_LED);

// ====================================================================
// VARIÁVEIS GLOBAIS
// ====================================================================
unsigned long ultimoEnvioUSB = 0; // Controle de tempo assíncrono

USBHID HID;
HIDJoystick Joystick(HID);

// ====================================================================
// FUNÇÕES DE FEEDBACK VISUAL
// ====================================================================
void aguardarEConfirmarConexaoUSB() {
  // 1. Fica piscando lentamente caso o cabo USB não esteja conectado
  // ou o Windows ainda esteja instalando o driver
  while (!USBComposite.isReady()) {
    statusLed.on();
    delay(300);
    statusLed.off();
    delay(300);
  }

  // Pequena pausa para separar as fases visuais
  delay(500);

  // 2. Cabo reconhecido! Pisca 3 vezes de forma bem visível
  statusLed.blink(3, 150, 150);

  // 3. Deixa aceso permanentemente para indicar sistema rodando
  statusLed.on();
}

// ====================================================================
// SETUP
// ====================================================================
void setup() {
  statusLed.begin();   // LED inicia apagado

  encoder.begin();     // configura pinos + interrupções do encoder
  axisY.begin();
  axisZ.begin();

  // Setup USB
  USBComposite.clear();
  USBComposite.setProductString("Joystick_Luciano");
  HID.begin(HID_JOYSTICK);

  // Chama a função de feedback
  aguardarEConfirmarConexaoUSB();
}

// ====================================================================
// LOOP PRINCIPAL
// ====================================================================
void loop() {
  // 1. LEITURA DOS PEDAIS E ENVIO USB (Controlado por tempo real, a cada 10ms)
  // O encoder não precisa mais ser lido aqui: a leitura acontece por
  // interrupção dentro de FFBCommon, então o valor já está sempre
  // atualizado quando chegamos neste ponto.
  if (millis() - ultimoEnvioUSB >= 10) {

    int valorY = axisY.read8();
    int valorZ = axisZ.read8();

    Joystick.X((int)encoder.getPosition());
    Joystick.sliderLeft(valorY);
    Joystick.sliderRight(valorZ);

    ultimoEnvioUSB = millis();
  }
}
