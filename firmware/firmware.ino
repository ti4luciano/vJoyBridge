#include <Arduino.h>
#include <USBComposite.h>

// ==================================================================
// CLASSES EMBUTIDAS (FFBCommon)
// ==================================================================
class QuadratureEncoder {
  public:
    QuadratureEncoder(uint8_t pinA, uint8_t pinB, int32_t minValue, int32_t maxValue, bool fullStepMode = false)
      : _pinA(pinA), _pinB(pinB), _min(minValue), _max(maxValue), _fullStepMode(fullStepMode), _position(0), _prevState(0), _accum(0) {}

    void begin() {
      pinMode(_pinA, INPUT_PULLUP);
      pinMode(_pinB, INPUT_PULLUP);
      _prevState = (digitalRead(_pinB) << 1) | digitalRead(_pinA);
      selfPtr() = this;
      // Configuração de interrupção (Crítico)
      attachInterrupt(digitalPinToInterrupt(_pinA), isrTrampoline, CHANGE);
      attachInterrupt(digitalPinToInterrupt(_pinB), isrTrampoline, CHANGE);
    }

    int32_t getPosition() const {
      noInterrupts();
      int32_t pos = _position;
      interrupts();
      return pos;
    }

    void setPosition(int32_t pos) {
      noInterrupts();
      _position = pos;
      clampInternal();
      interrupts();
    }

    void reset() { setPosition(0); }

  private:
    uint8_t  _pinA, _pinB;
    int32_t  _min, _max;
    bool     _fullStepMode;
    volatile int32_t _position;
    volatile uint8_t _prevState;
    volatile int8_t  _accum;

    void clampInternal() {
      if (_position > _max) _position = _max;
      if (_position < _min) _position = _min;
    }

    void handleInterrupt() {
      static const int8_t table[16] = {
         0,  1, -1,  0,
        -1,  0,  0,  1,
         1,  0,  0, -1,
         0, -1,  1,  0
      };
      uint8_t a = digitalRead(_pinA);
      uint8_t b = digitalRead(_pinB);
      uint8_t currentState = (b << 1) | a;
      uint8_t index = (_prevState << 2) | currentState;
      int8_t delta = table[index];

      if (delta != 0) {
        if (!_fullStepMode) {
          _position += delta;
          clampInternal();
        } else {
          _accum += delta;
          if (currentState == 3) {
            int8_t steps = _accum / 4;
            if (steps != 0) {
              _position += steps;
              clampInternal();
            }
            _accum = 0;
          }
        }
      }
      _prevState = currentState;
    }

    static QuadratureEncoder*& selfPtr() {
      static QuadratureEncoder* instance = nullptr;
      return instance;
    }

    static void isrTrampoline() {
      QuadratureEncoder* instance = selfPtr();
      if (instance) instance->handleInterrupt();
    }
};

class AnalogAxis {
  public:
    explicit AnalogAxis(uint8_t pin, float smoothing = 0.0f)
      : _pin(pin), _smoothing(smoothing), _filtered(0), _first(true) {}

    void begin() {
      pinMode(_pin, INPUT_ANALOG);
    }

    int16_t readRaw() {
      int raw = analogRead(_pin);
      if (_smoothing <= 0.0f) return raw;
      if (_first) {
        _filtered = raw;
        _first = false;
      } else {
        _filtered = _filtered + (1.0f - _smoothing) * (raw - _filtered);
      }
      return (int16_t)_filtered;
    }

    int16_t read8() {
      return readRaw() / 4;
    }

  private:
    uint8_t _pin;
    float   _smoothing;
    float   _filtered;
    bool    _first;
};

class StatusLed {
  public:
    explicit StatusLed(uint8_t pin, bool activeLow = true)
      : _pin(pin), _activeLow(activeLow) {}

    void begin() {
      pinMode(_pin, OUTPUT);
      off();
    }

    void on()  { digitalWrite(_pin, _activeLow ? LOW : HIGH); }
    void off() { digitalWrite(_pin, _activeLow ? HIGH : LOW); }
    void toggle() { digitalWrite(_pin, !digitalRead(_pin)); }

    void blink(uint8_t times, uint16_t onMs, uint16_t offMs) {
      for (uint8_t i = 0; i < times; i++) {
        on();
        delay(onMs);
        off();
        delay(offMs);
      }
    }
  private:
    uint8_t _pin;
    bool    _activeLow;
};

// ==================================================================
// DEFINIÇÕES DE HARDWARE (Nomenclatura STM32)
// ==================================================================
#define PIN_ENC_CLK PA1
#define PIN_ENC_DT  PA0
#define PIN_POT_Y   PB1
#define PIN_POT_Z   PB0
#define PIN_LED     PC13

#define MOTOR_IN1   PA3
#define MOTOR_IN2   PA2

#define PIN_BTN1 PB5
#define PIN_BTN2 PB6
#define PIN_BTN3 PB7
#define PIN_BTN4 PB8
#define PIN_BTN5 PB9
#define PIN_BTN6 PB10
#define PIN_BTN7 PB11
#define PIN_BTN8 PB12

#define LIMITE_MIN 0
#define LIMITE_MAX 512

// ==================================================================
// VARIÁVEIS GLOBAIS E OBJETOS
// ==================================================================
QuadratureEncoder encoder(PIN_ENC_CLK, PIN_ENC_DT, LIMITE_MIN, LIMITE_MAX);
AnalogAxis axisY(PIN_POT_Y);
AnalogAxis axisZ(PIN_POT_Z);
StatusLed  statusLed(PIN_LED);

USBHID HID;
HIDJoystick Joystick(HID);

bool isSerialMode = false;
unsigned long lastSendTime = 0;
const unsigned long SEND_INTERVAL_MS_SERIAL = 100;
const unsigned long SEND_INTERVAL_MS_USB = 10;

char rxBuffer[32];
uint8_t rxIndex = 0;

void parseSerialCommand(char* cmd);
void setMotorForce(int pwm, int dir);
uint8_t readButtons();

// ==================================================================
// SETUP
// ==================================================================
void setup() {
  statusLed.begin();
  
  // Ponto Chave: Início do Boot
  statusLed.blink(1, 1000, 100); 

  pinMode(MOTOR_IN1, OUTPUT);
  pinMode(MOTOR_IN2, OUTPUT);
  digitalWrite(MOTOR_IN1, LOW);
  digitalWrite(MOTOR_IN2, LOW);

  pinMode(PIN_BTN1, INPUT_PULLUP);
  pinMode(PIN_BTN2, INPUT_PULLUP);
  pinMode(PIN_BTN3, INPUT_PULLUP);
  pinMode(PIN_BTN4, INPUT_PULLUP);
  pinMode(PIN_BTN5, INPUT_PULLUP);
  pinMode(PIN_BTN6, INPUT_PULLUP);
  pinMode(PIN_BTN7, INPUT_PULLUP);
  pinMode(PIN_BTN8, INPUT_PULLUP);

  axisY.begin();
  axisZ.begin();
  encoder.begin(); 

  Serial.begin(115200);

  // Janela de Handshake (3 Segundos)
  unsigned long bootTime = millis();
  while (millis() - bootTime < 3000) {
    if (Serial.available() > 0) {
      char c = Serial.read();
      if (c == 'H') { 
        isSerialMode = true;
        break;
      }
    }
  }

  if (isSerialMode) {
    // Ponto Chave: Sucesso no Handshake / Modo Serial
    statusLed.blink(2, 200, 200);
    statusLed.on(); // Mantém aceso para indicar Serial
  } else {
    // Desliga CDC para usar interface HID nativa
    Serial.end();

    USBComposite.clear();
    USBComposite.setProductString("Joystick_Luciano");
    HID.begin(HID_JOYSTICK);

    // Ponto Chave: Aguardando Host USB
    while (!USBComposite.isReady()) {
      statusLed.toggle();
      delay(300);
    }
    
    // Ponto Chave: Host USB conectado / Modo USB
    statusLed.off();
    delay(200);
    statusLed.blink(3, 200, 200);
    statusLed.on(); // Mantém aceso para indicar USB
  }
}

// ==================================================================
// LOOP PRINCIPAL
// ==================================================================
void loop() {
  unsigned long currentTime = millis();

  if (isSerialMode) {
    while (Serial.available() > 0) {
      char c = Serial.read();
      statusLed.toggle(); // Ponto de Debug: Recepção Serial

      if (c == '\n') {
        rxBuffer[rxIndex] = '\0';
        parseSerialCommand(rxBuffer);
        rxIndex = 0;
      }
      else if (c != '\r' && rxIndex < 31) {
        rxBuffer[rxIndex++] = c;
      }
    }

    if (currentTime - lastSendTime >= SEND_INTERVAL_MS_SERIAL) {
      lastSendTime = currentTime;
      Serial.print("X:");
      Serial.print((int16_t)encoder.getPosition());
      Serial.print(" Y:");
      Serial.print(axisY.read8());
      Serial.print(" Z:");
      Serial.print(axisZ.read8());
      Serial.print(" B:");
      Serial.println(readButtons());
    }
  } else {
    if (currentTime - lastSendTime >= SEND_INTERVAL_MS_USB) {
      lastSendTime = currentTime;
      Joystick.X((int)encoder.getPosition());
      Joystick.sliderLeft(axisY.read8());
      Joystick.sliderRight(axisZ.read8());
      
      uint8_t btns = readButtons();
      for(uint8_t i = 0; i < 8; i++) {
        Joystick.button(i + 1, (btns >> i) & 0x01);
      }
    }
  }
}

// ==================================================================
// FUNÇÕES AUXILIARES
// ==================================================================
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

uint8_t readButtons() {
  uint8_t state = 0;
  state |= (!digitalRead(PIN_BTN1)) << 0;
  state |= (!digitalRead(PIN_BTN2)) << 1;
  state |= (!digitalRead(PIN_BTN3)) << 2;
  state |= (!digitalRead(PIN_BTN4)) << 3;
  state |= (!digitalRead(PIN_BTN5)) << 4;
  state |= (!digitalRead(PIN_BTN6)) << 5;
  state |= (!digitalRead(PIN_BTN7)) << 6;
  state |= (!digitalRead(PIN_BTN8)) << 7;
  return state;
}