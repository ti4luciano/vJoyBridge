//Usar a configuração de placa STM32F1xx -> STM32F103C6/fake
/*
 * Firmware FFB - Ponte Serial para BackForceFeeder
 * MCU: STM32F103C6
 * IDE: Arduino IDE (STM32duino)
 * Leitura: Interrupção por Matriz de Estados (Anti-Bouncing Otimizado)
 */

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
#define LIMITE_DIREITO 162

// ==========================================================
// Variáveis Globais
// ==========================================================
unsigned long lastSendTime = 0;
const unsigned long SEND_INTERVAL_MS = 100;

char rxBuffer[32];
uint8_t rxIndex = 0;

// Variáveis do Encoder (Volatile pois são alteradas na interrupção)
volatile int32_t encoderPos = 0;
volatile uint8_t encoderPrevState = 0;

/* 
 * Matriz de Estados do Encoder
 * Linhas (estado anterior), Colunas (estado atual)
 * Ignora saltos inválidos (ex: 00 -> 11) mapeando-os como 0.
 */
const int8_t encoderMatrix[16] = {
  0,  1, -1,  0,  // Estado anterior 00
 -1,  0,  0,  1,  // Estado anterior 01
  1,  0,  0, -1,  // Estado anterior 10
  0, -1,  1,  0   // Estado anterior 11
};

// ==========================================================
// Declaração de Funções
// ==========================================================
void setupHardwareEncoder();
void parseSerialCommand(char* cmd);
void setMotorForce(int pwm, int dir);
void encoderISR();

// ==========================================================
// SETUP
// ==========================================================
void setup() {
  Serial.begin(115200); 
  
  pinMode(MOTOR_IN1, OUTPUT);
  pinMode(MOTOR_IN2, OUTPUT);
  digitalWrite(MOTOR_IN1, LOW);
  digitalWrite(MOTOR_IN2, LOW);

  // Configura pinos do Encoder
  pinMode(ENCODER_PIN_A, INPUT_PULLUP);
  pinMode(ENCODER_PIN_B, INPUT_PULLUP);

  // Configuração do LED de Log
  pinMode(LED_PIN, OUTPUT);
  digitalWrite(LED_PIN, HIGH); // Inicia com o LED apagado

  // Potenciômetros
  pinMode(DESLIZ_A, INPUT_ANALOG);
  pinMode(DESLIZ_B, INPUT_ANALOG);

  // Lê o estado inicial diretamente do registrador do GPIOA
  // PA0 é o bit 0 e PA1 é o bit 1 (Máscara 0x03 isola ambos)
  encoderPrevState = (digitalRead(PA1) << 1) | digitalRead(PA0);

  // Atrela as interrupções para qualquer mudança de estado (CHANGE)
  attachInterrupt(digitalPinToInterrupt(ENCODER_PIN_A), encoderISR, CHANGE);
  attachInterrupt(digitalPinToInterrupt(ENCODER_PIN_B), encoderISR, CHANGE);
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
    digitalToggle(LED_PIN);
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
    
    // Desabilita interrupções momentaneamente para uma cópia segura (Atomic Read)
    noInterrupts();
    int16_t currentPos = (int16_t)encoderPos;
    int valorY = analogRead(DESLIZ_A) / 4;
    int valorZ = analogRead(DESLIZ_B) / 4;
    interrupts();
    
    Serial.print("X:");
    Serial.print(currentPos);
    Serial.print(" Y:");
    Serial.print(valorY);
    Serial.print(" Z:");
    Serial.println(valorZ);
  }
}

// ==========================================================
// INTERRUPÇÃO DO ENCODER (ISR)
// ==========================================================
void encoderISR() {
  // Lê PA0 e PA1 em uma única instrução de clock para evitar dessincronia
  uint8_t pa0 = digitalRead(PA0);
  uint8_t pa1 = digitalRead(PA1);
  uint8_t currentState = (pa1 << 1) | pa0;
  
  // Combina estado anterior (bits 3 e 2) com atual (bits 1 e 0) para formar o índice
  uint8_t index = (encoderPrevState << 2) | currentState;

  // Incrementa ou decrementa usando a tabela. Se for ruído, soma 0.
  encoderPos += encoderMatrix[index];

  if (encoderPos > LIMITE_DIREITO) encoderPos = LIMITE_DIREITO;
  if (encoderPos < LIMITE_ESQUERDO) encoderPos = LIMITE_ESQUERDO;

  encoderPrevState = currentState;
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

void digitalToggle(uint8_t pin) {
    digitalWrite(pin, !digitalRead(pin));
}