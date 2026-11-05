$root = $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $root ".tools\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:JAVA_HOME = (Get-ChildItem (Join-Path $root ".tools\jdk17t") -Directory | Select-Object -First 1).FullName
$env:ANDROID_HOME = Join-Path $root ".tools\android-sdk"
