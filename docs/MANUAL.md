# Manual — instalação, diagnóstico e ADB

## Compilar
```powershell
. .\tools-env.ps1        # usa SDK .NET 9, JDK 17 e Android SDK locais em .tools\
dotnet test tests\CeratoSoundAI.Tests
dotnet publish src\CeratoSoundAI.App -c Release -f net9.0-android -p:AndroidPackageFormat=apk -p:RuntimeIdentifier=android-arm64 -p:AndroidSdkDirectory=$env:ANDROID_HOME -p:JavaSdkDirectory=$env:JAVA_HOME
```
Use `android-arm` para centrais 32-bit. APKs prontos em `dist\` (assinados com chave de debug).

## Instalar
Pendrive/ADB: `adb install -r CeratoSoundAI-armeabi-v7a.apk` (ou arm64). Descubra a ABI: `adb shell getprop ro.product.cpu.abi`.

## Diagnóstico
1. Abra o app → **Diagnóstico** → Executar. 2. Compartilhe o JSON (dados sensíveis são removidos). 3. Cole os achados em HARDWARE_RESEARCH.md.

## Comandos ADB somente leitura
```
adb shell getprop ro.product.model
adb shell getprop ro.build.fingerprint
adb shell dumpsys audio
adb shell dumpsys media.audio_flinger
adb shell dumpsys media.audio_policy
adb shell pm list packages | findstr /i "eq dsp audio sound"
adb shell cat /proc/cpuinfo
adb shell cat /proc/meminfo
```
Não execute comandos de escrita/proprietários.

## Testes em veículo (registrar resultado)
1. Aplicar perfil, desligar ignição, religar: perfil reaplicado? Som mudou de fato (ouvir A/B)?
2. Suspensão/retomada. 3. Reboot completo. 4. Encerrar app à força. 5. Queda/retorno de energia.
Só declare persistência após esses testes.

## Segurança
Calibração apenas com veículo parado; volume baixo; sinal de teste ≤ −20 dBFS. Chave Ollama é digitada em Configurações (SecureStorage) — prefira backend Samos.
