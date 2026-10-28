# Installs the Android SDK for building the mobile app (Windows).
# Needs JDK 21 (winget install Microsoft.OpenJDK.21).
# Usage: powershell -ExecutionPolicy Bypass -File .\tools\setup-android-sdk.ps1
$ErrorActionPreference = 'Stop'

$sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
$jdk = Get-ChildItem 'C:\Program Files\Microsoft' -Filter 'jdk-21*' -Directory | Select-Object -First 1 -ExpandProperty FullName
if (-not $jdk) { throw 'JDK 21 not found. Run: winget install Microsoft.OpenJDK.21' }

$env:JAVA_HOME = $jdk
$env:ANDROID_HOME = $sdk
[Environment]::SetEnvironmentVariable('JAVA_HOME', $jdk, 'User')
[Environment]::SetEnvironmentVariable('ANDROID_HOME', $sdk, 'User')
Write-Host "JAVA_HOME    = $jdk"
Write-Host "ANDROID_HOME = $sdk"

$tools = Join-Path $sdk 'cmdline-tools\latest'
$sm = Join-Path $tools 'bin\sdkmanager.bat'
if (-not (Test-Path $sm)) {
    Write-Host ''
    Write-Host '== Step 1/3: downloading Android command-line tools (~150 MB)' -ForegroundColor Cyan
    $zip = Join-Path $env:TEMP 'android-cmdline-tools.zip'
    curl.exe -L --fail --progress-bar -o $zip 'https://dl.google.com/android/repository/commandlinetools-win-13114758_latest.zip'
    if ($LASTEXITCODE -ne 0) { throw 'Download failed' }
    $tmp = Join-Path $env:TEMP 'android-cmdline-tools'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Write-Host 'Unpacking...'
    Expand-Archive $zip $tmp
    New-Item -ItemType Directory -Force (Split-Path $tools) | Out-Null
    Move-Item (Join-Path $tmp 'cmdline-tools') $tools
}

Write-Host ''
Write-Host '== Step 2/3: Android SDK licenses. Read each one and type y to accept.' -ForegroundColor Cyan
& $sm --licenses

Write-Host ''
Write-Host '== Step 3/3: downloading SDK packages (~1 GB)' -ForegroundColor Cyan
& $sm 'platform-tools' 'platforms;android-36' 'build-tools;36.0.0'
if ($LASTEXITCODE -ne 0) { throw 'sdkmanager failed' }

Write-Host ''
Write-Host "== Done. Android SDK is in $sdk" -ForegroundColor Green
