//Usar a configuração de placa STM32F1xx -> STM32F103C6/fake

#include <USBComposite.h>

#define PIN_ENC_CLK PA0
#define PIN_ENC_DT  PA1
#define PIN_POT_Y   PB1
#define PIN_POT_Z   PB0
#define PIN_LED     PC13

// ====================================================================
// VARIÁVEIS GLOBAIS
// ====================================================================
int encoderPosicao = 0;
uint8_t estadoAnterior = 0;
unsigned long ultimoEnvioUSB = 0; // Controle de tempo assíncrono

USBHID HID;
HIDJoystick Joystick(HID);

// Matriz de Quadratura para leitura perfeita do Encoder
const int8_t tabelaDecodificacao[] = {
  0,  1, -1,  0,
 -1,  0,  0,  1,
  1,  0,  0, -1,
  0, -1,  1,  0 
};

// ====================================================================
// FUNÇÕES DE FEEDBACK VISUAL
// ====================================================================
void aguardarEConfirmarConexaoUSB() {
  // 1. Fica piscando lentamente caso o cabo USB não esteja conectado 
  // ou o Windows ainda esteja instalando o driver
  while (!USBComposite.isReady()) {
    digitalWrite(PIN_LED, LOW);  // Acende
    delay(300);
    digitalWrite(PIN_LED, HIGH); // Apaga
    delay(300);
  }

  // Pequena pausa para separar as fases visuais
  delay(500);

  // 2. Cabo reconhecido! Pisca 3 vezes de forma bem visível
  for (int i = 0; i < 3; i++) {
    digitalWrite(PIN_LED, LOW);  
    delay(150);
    digitalWrite(PIN_LED, HIGH); 
    delay(150);
  }
  
  // 3. Deixa aceso permanentemente para indicar sistema rodando
  digitalWrite(PIN_LED, LOW); 
}

// ====================================================================
// SETUP
// ====================================================================
void setup() {
  pinMode(PIN_LED, OUTPUT);
  digitalWrite(PIN_LED, HIGH); // Garante que inicia apagado

  // Encoder
  pinMode(PIN_ENC_CLK, INPUT_PULLUP);
  pinMode(PIN_ENC_DT, INPUT_PULLUP);
  
  // Limpa o estado inicial do encoder
  estadoAnterior = (digitalRead(PIN_ENC_CLK) << 1) | digitalRead(PIN_ENC_DT);

  // Potenciômetros
  pinMode(PIN_POT_Y, INPUT_ANALOG);
  pinMode(PIN_POT_Z, INPUT_ANALOG);

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
  // 1. LEITURA DO ENCODER (Velocidade Máxima)
  uint8_t estadoAtual = (digitalRead(PIN_ENC_CLK) << 1) | digitalRead(PIN_ENC_DT);
  
  if (estadoAtual != estadoAnterior) {
    uint8_t indice = (estadoAnterior << 2) | estadoAtual;
    int8_t movimento = tabelaDecodificacao[indice];

    if (movimento != 0) {
      encoderPosicao += movimento;
      
      // Limites lógicos do Joystick
      if (encoderPosicao > 512) encoderPosicao = 512;
      if (encoderPosicao < 0)    encoderPosicao = 0;
    }
    
    estadoAnterior = estadoAtual;
  }

  // 2. LEITURA DOS PEDAIS E ENVIO USB (Controlado por tempo real, a cada 10ms)
  if (millis() - ultimoEnvioUSB >= 10) {
    
    int valorY = analogRead(PIN_POT_Y) / 4;
    int valorZ = analogRead(PIN_POT_Z) / 4;

    Joystick.X(encoderPosicao); 
    Joystick.sliderLeft(valorY);
    Joystick.sliderRight(valorZ);

    ultimoEnvioUSB = millis();
  }
}