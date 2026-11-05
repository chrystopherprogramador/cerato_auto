# ROADMAP

## Feito (compila, testes unitários OK)
- Solution em camadas, UI premium, SQLite, engines (Android/Manual/Simulação/Nativo-stub), perfis, IA Ollama, diagnóstico, calibração básica, voz.

## Pendente
- [ ] Executar o Diagnóstico na Wintech e preencher HARDWARE_RESEARCH.md com dados reais.
- [ ] Analisar wDSP, allEQ, RootlessJamesDSP, android-head-unit-exploration.
- [ ] Identificar MCU/DSP; só então avaliar NativeDspAudioEngine.
- [ ] Testes em veículo: ignição off, suspensão, reboot, force-stop, queda de energia, efeito audível.
- [ ] Fonte de movimento do veículo (CAN/GPS) para bloquear operações em movimento.
- [ ] Análise espectral (FFT) e geração de filtros na calibração; microfone externo.
- [ ] Mais testes de integração do motor Android (instrumentados, em dispositivo).
- [ ] Assinatura de release própria (APK atual usa chave de debug); reduzir tamanho (AndroidLinkMode).
- [ ] Backend intermediário Samos para proteger a chave Ollama.
- [ ] Testes de UI em 1024x600 no dispositivo real.
