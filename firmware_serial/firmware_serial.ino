//Usar a configuração de placa STM32F1xx -> STM32F103C6/fake
/*
 * Firmware FFB - Ponte Serial para BackForceFeeder
 * MCU: STM32F103C6
 * IDE: Arduino IDE (STM32duino)
 * Leitura: Interrupção por Matriz de Estados (Anti-Bouncing Otimizado)
 *
 * Este firmware usa a biblioteca compartilhada FFBCommon (mesma
 * usada no firmware USB) para leitura do encoder, dos potenciometros
 * e controle do LED. A logica especifica deste firmware (protocolo
 * serial texto + controle da ponte H do motor) permanece aqui.
 *
 * Requer a biblioteca FFBCommon instalada (Sketch > Include Library
 * > Add .ZIP Library... - ver README).
 */

#include <FFBCommon.h>

// ==========================================================
// Configuração de Pinos
// ==========================================================
#define ENCODER_PIN_A PA0
#define ENCODER_PIN_B PA1
#define DESLIZ_A      PB1
#define DESLIZ_B      PB0

// Pinos da Ponte H MX1616
#define MOTOR_IN1 PA2
#define MOTOR_IN2 PA3

#define LED_PIN PC13

#define LIMITE_ESQUERDO -162
#define LIMITE_DIREITO   162

// ==========================================================
// Objetos compartilhados (FFBCommon)
// ==========================================================
QuadratureEncoder encoder(ENCODER_PIN_A, ENCODER_PIN_B, LIMITE_ESQUERDO, LIMITE_DIREITO);
AnalogAxis axisY(DESLIZ_A);
AnalogAxis axisZ(DESLIZ_B);
StatusLed  statusLed(LED_PIN);

// ==========================================================
// Variáveis Globais
// ==========================================================
unsigned long lastSendTime = 0;
const unsigned long SEND_INTERVAL_MS = 100;

char rxBuffer[32];
uint8_t rxIndex = 0;

// ==========================================================
// Declaração de Funções
// ==========================================================
void parseSerialCommand(char* cmd);
void setMotorForce(int pwm, int dir);

// ==========================================================
// SETUP
// ==========================================================
void setup() {
  Serial.begin(115200);

  pinMode(MOTOR_IN1, OUTPUT);
  pinMode(MOTOR_IN2, OUTPUT);
  digitalWrite(MOTOR_IN1, LOW);
  digitalWrite(MOTOR_IN2, LOW);

  statusLed.begin();   // LED inicia apagado

  axisY.begin();
  axisZ.begin();

  encoder.begin();     // configura pinos + interrupções do encoder
}

// ==========================================================
// LOOP PRINCIPAL
// ==========================================================
void loop() {

  // 1. LEITURA DE ENTRADA (PC -> STM32)
  while (Serial.available() > 0) {
    char c = Serial.read();

    // Inverte o estado do LED a cada comando recebido
    // Como os comandos chegam rápido, o LED vai parecer estar piscando/dimmerizado
    statusLed.toggle();

    if (c == '\n') {
      rxBuffer[rxIndex] = '\0';
      parseSerialCommand(rxBuffer);
      rxIndex = 0;
    }
    else if (c != '\r' && rxIndex < 31) {
      rxBuffer[rxIndex++] = c;
    }
  }

  // 2. COMUNICAÇÃO DE SAÍDA (STM32 -> PC)
  unsigned long currentTime = millis();
  if (currentTime - lastSendTime >= SEND_INTERVAL_MS) {
    lastSendTime = currentTime;

    int16_t currentPos = (int16_t)encoder.getPosition();
    int valorY = axisY.read8();
    int valorZ = axisZ.read8();

    Serial.print("X:");
    Serial.print(currentPos);
    Serial.print(" Y:");
    Serial.print(valorY);
    Serial.print(" Z:");
    Serial.println(valorZ);
  }
}

// ==========================================================
// IMPLEMENTAÇÕES DE MOTOR / PARSE
// ==========================================================
void parseSerialCommand(char* cmd) {
  if (cmd[0] == 'F' && cmd[1] == ':') {
    char* ptr = cmd + 2;
    int pwmValue = 0;

    while (*ptr >= '0' && *ptr <= '9') {
      pwmValue = (pwmValue * 10) + (*ptr - '0');
      ptr++;
    }

    if (*ptr == ',') {
      ptr++;
      int dirValue = (*ptr == '1') ? 1 : 0;
      setMotorForce(pwmValue, dirValue);
    }
  }
}

void setMotorForce(int pwm, int dir) {
  if (pwm > 255) pwm = 255;
  if (pwm < 0) pwm = 0;

  if (dir == 1) {
    analogWrite(MOTOR_IN2, 0);
    analogWrite(MOTOR_IN1, pwm);
  } else {
    analogWrite(MOTOR_IN1, 0);
    analogWrite(MOTOR_IN2, pwm);
  }
}
