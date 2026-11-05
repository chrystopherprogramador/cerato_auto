# HARDWARE_RESEARCH — Cerato Sound AI

Status: **nenhuma medição feita em veículo ainda.** Tudo abaixo é fato do código, hipótese ou limitação; está rotulado.

## 1. Fatos verificados (ambiente de desenvolvimento)
- Projeto compila para Android (net9.0-android, minSdk 21); 29 testes unitários passam.
- O app **nunca** foi executado na Wintech Evolution. Chipset, MCU, versão do Android e app de EQ original são **desconhecidos** até rodar o Diagnóstico.

## 2. Camadas de processamento (conceito)
| Camada | O que é | Controlável pelo app? |
|---|---|---|
| Android AudioEffect (Equalizer/BassBoost/…) | API pública, sessão de áudio | Sim, mas só afeta o som se a ROM/central rotear a sessão (sessão global 0 está obsoleta e pode ser ignorada). **Efeito audível não é provado por leitura de retorno.** |
| EQ/DSP proprietário do app original (“ASP”) | App do fabricante conversa com serviço/driver próprio | **Não sem identificar a interface.** NativeDspAudioEngine permanece *Indisponível*. |
| DSP/MCU externo | Chip separado (comandos via serial/I²C) | **Não** — requer reverse engineering, fora do escopo do MVP. |
| Amplificador de fábrica | Hardware | Não. |

## 3. Hipóteses (NÃO confirmadas)
1. A Wintech pode usar um EQ próprio ligado à MCU; o AudioEffect Android pode não ter efeito.
2. Se houver EQ original ativo + EQ do app, haverá processamento duplo. O app avisa, não neutraliza.
3. Perda de qualidade vs. original pode vir de: polaridade/fiação, balance/fader, clipping, ganho de entrada, fonte. Use a seção de Diagnóstico de qualidade.

## 4. Referências a estudar (sem presumir compatibilidade)
- khiivl/wDSP, omixin/allEQ — abordagens de EQ em head units chinesas; verificar chipset/MCU antes de reaproveitar.
- RootlessJamesDSP (F-Droid) — DSP sem root via captura de áudio; exige APIs (MediaProjection/AudioPlaybackCapture, API 29+) provavelmente ausentes em Android antigo.
- andresurena/android-head-unit-exploration — mapa de serviços/MCU em head units; comparar com o relatório do Diagnóstico.
(Nota: estes repositórios ainda não foram analisados em profundidade nesta etapa — ver ROADMAP.)

## 5. Limitações conhecidas
- Sem root/Shizuku/firmware: nada de acesso a MCU.
- Persistência após desligar a ignição: o perfil é salvo em SQLite (WAL, synchronous=FULL), reaplicado ao abrir/retomar e via BOOT_COMPLETED, **mas permanência do efeito sem o app aberto NÃO está comprovada**.
- Calibração: microfone interno da central não é calibrado; sinais de teste limitados a −20 dBFS/10 s; exige confirmação de veículo parado.
- IA: só propõe `set_band`, `set_preamp`, `revert`; nada é aplicado sem confirmação.

## 5b. Depende de identificação da MCU / testes no veículo
- Controle nativo do DSP; crossover; time alignment; subwoofer; balance/fader por hardware.
- Efeito audível do Equalizer Android; reaplicação após boot; suspensão; kill do app; queda de energia.
