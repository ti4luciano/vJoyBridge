/*
 * FFBCommon.h
 * ------------------------------------------------------------
 * Biblioteca compartilhada entre os firmwares FFB (ponte Serial
 * e Joystick USB) rodando em STM32F103C6.
 *
 * Contem tudo que os dois firmwares tinham em comum:
 *   - QuadratureEncoder : leitura do encoder por interrupcao
 *   - AnalogAxis        : leitura suavizada de potenciometros/pedais
 *   - StatusLed         : controle do LED de status (PC13)
 *
 * E "header-only" (toda a implementacao fica dentro da propria
 * classe, aqui neste unico arquivo) de proposito, pra biblioteca
 * ficar simples de olhar/editar - nao ha um FFBCommon.cpp separado.
 *
 * Melhoria principal em relacao ao codigo original:
 *   O firmware USB antigo lia o encoder por polling dentro do
 *   loop() (sem interrupcao). Isso significa que qualquer bloqueio
 *   no loop (ex.: envio USB, delay do LED) podia deixar passar
 *   transicoes do encoder e "perder passos" em giros rapidos.
 *   Agora os dois firmwares leem o encoder por interrupcao (CHANGE
 *   em ambos os pinos), igual ao que ja era feito no firmware
 *   Serial, garantindo leitura consistente independente do que
 *   acontece no loop principal.
 *
 *   Alem disso foi adicionado um modo opcional "full-step" (passo
 *   completo) para encoders com detent mecanico: em vez de contar
 *   as 4 transicoes eletricas de cada clique (o que pode gerar
 *   contagem residual caso o giro pare no meio de uma transicao),
 *   o modo full-step so confirma o movimento quando o encoder volta
 *   ao estado de repouso, eliminando contagens fantasmas. E opcional
 *   e desligado por padrao para nao alterar o comportamento/calibra-
 *   cao ja validada nas duas maquinas.
 *
 * Observacao: cada instancia de QuadratureEncoder usa uma variavel
 * estatica interna (Meyer's singleton) para saber "quem sou eu"
 * dentro da interrupcao. Isso funciona perfeitamente para o caso de
 * uso atual (1 encoder por sketch). Se um dia for necessario usar
 * 2 encoders ao mesmo tempo no MESMO sketch, essa parte precisa ser
 * adaptada para suportar multiplas instancias.
 * ------------------------------------------------------------
 */

#ifndef FFBCOMMON_H
#define FFBCOMMON_H

#include <Arduino.h>

// ==================================================================
// QuadratureEncoder
// ==================================================================
class QuadratureEncoder {
  public:
    // pinA/pinB          : pinos do encoder (ex.: PA0/PA1)
    // minValue/maxValue   : limites de saturacao da posicao
    // fullStepMode        : true = so confirma o movimento a cada
    //                       ciclo completo (detent). Default: false
    //                       (mantem a resolucao x4 original).
    QuadratureEncoder(uint8_t pinA, uint8_t pinB,
                       int32_t minValue, int32_t maxValue,
                       bool fullStepMode = false)
      : _pinA(pinA), _pinB(pinB),
        _min(minValue), _max(maxValue),
        _fullStepMode(fullStepMode),
        _position(0), _prevState(0), _accum(0)
    {
    }

    // Configura pinMode (INPUT_PULLUP) e liga as interrupcoes.
    // Chamar uma vez, no setup().
    void begin() {
      pinMode(_pinA, INPUT_PULLUP);
      pinMode(_pinB, INPUT_PULLUP);

      // Estado inicial (bit1 = pinB, bit0 = pinA).
      _prevState = (digitalRead(_pinB) << 1) | digitalRead(_pinA);

      selfPtr() = this;
      attachInterrupt(digitalPinToInterrupt(_pinA), isrTrampoline, CHANGE);
      attachInterrupt(digitalPinToInterrupt(_pinB), isrTrampoline, CHANGE);
    }

    // Leitura atomica da posicao (thread-safe em relacao a ISR).
    int32_t getPosition() const {
      noInterrupts();
      int32_t pos = _position;
      interrupts();
      return pos;
    }

    // Ajusta a posicao manualmente (ex.: centralizar apos calibracao).
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
    volatile int8_t  _accum;   // acumulador de sub-passos (modo full-step)

    void clampInternal() {
      if (_position > _max) _position = _max;
      if (_position < _min) _position = _min;
    }

    void handleInterrupt() {
      // Matriz de estados: linhas = estado anterior, colunas = estado
      // atual. Transicoes invalidas (ruido/salto de 2 bits) mapeadas
      // como 0 - mesma tabela dos dois firmwares originais.
      static const int8_t table[16] = {
         0,  1, -1,  0,   // 00
        -1,  0,  0,  1,   // 01
         1,  0,  0, -1,   // 10
         0, -1,  1,  0    // 11
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
          // So confirma o movimento quando volta ao repouso
          // (currentState == 3, ambas as linhas em nivel alto).
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

    // Ponteiro estatico "local a funcao" (Meyer's singleton): evita
    // precisar declarar/definir uma variavel estatica de classe em
    // outro arquivo. Guarda a (unica) instancia ativa para o
    // trampolim de interrupcao poder chamar handleInterrupt().
    static QuadratureEncoder*& selfPtr() {
      static QuadratureEncoder* instance = nullptr;
      return instance;
    }

    static void isrTrampoline() {
      QuadratureEncoder* instance = selfPtr();
      if (instance) instance->handleInterrupt();
    }
};

// ==================================================================
// AnalogAxis
// ------------------------------------------------------------------
// Leitura de potenciometro/pedal com filtro opcional de media movel
// exponencial (EMA), escalado para 0-255 (mesma escala ja usada nos
// dois firmwares: analogRead()/4).
// ==================================================================
class AnalogAxis {
  public:
    // smoothing = 0.0 -> sem filtro (identico ao codigo original)
    // smoothing = 0.5 a 0.9 -> filtro progressivamente mais forte
    explicit AnalogAxis(uint8_t pin, float smoothing = 0.0f)
      : _pin(pin), _smoothing(smoothing), _filtered(0), _first(true)
    {
    }

    void begin() {
      pinMode(_pin, INPUT_ANALOG);
    }

    // Valor bruto do ADC.
    int16_t readRaw() {
      int raw = analogRead(_pin);

      if (_smoothing <= 0.0f) {
        return raw;
      }

      if (_first) {
        _filtered = raw;
        _first = false;
      } else {
        _filtered = _filtered + (1.0f - _smoothing) * (raw - _filtered);
      }
      return (int16_t)_filtered;
    }

    // Valor filtrado, escalado para 0-255.
    int16_t read8() {
      return readRaw() / 4;
    }

  private:
    uint8_t _pin;
    float   _smoothing;
    float   _filtered;
    bool    _first;
};

// ==================================================================
// StatusLed
// ------------------------------------------------------------------
// LED de status (PC13 no Blue Pill / STM32F103C6), ativo em LOW.
// ==================================================================
class StatusLed {
  public:
    explicit StatusLed(uint8_t pin, bool activeLow = true)
      : _pin(pin), _activeLow(activeLow)
    {
    }

    void begin() {
      pinMode(_pin, OUTPUT);
      off();
    }

    void on()  { digitalWrite(_pin, _activeLow ? LOW : HIGH); }
    void off() { digitalWrite(_pin, _activeLow ? HIGH : LOW); }
    void toggle() { digitalWrite(_pin, !digitalRead(_pin)); }

    // Pisca "times" vezes (bloqueante, usado so em setup()).
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

#endif // FFBCOMMON_H
