# Сборка раздачи: dist\Couchpad-Setup-<версия>.exe (установщик для Windows, один файл со встроенным .NET)
# и dist\Couchpad-<версия>.apk (приложение, подписанное ключом из %USERPROFILE%\.couchpad\signing.properties).
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
$version = ([xml](Get-Content (Join-Path $root "agent\src\RemoteDesk.Agent\RemoteDesk.Agent.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

New-Item -ItemType Directory -Force $dist | Out-Null
$publish = Join-Path $dist "publish"
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Агент $version…"
& dotnet publish (Join-Path $root "agent\src\RemoteDesk.Agent\RemoteDesk.Agent.csproj") -c Release -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish: код $LASTEXITCODE" }
# Тот же файл: с «Setup» в имени он открывает окно установки.
Copy-Item (Join-Path $publish "Couchpad.exe") (Join-Path $dist "Couchpad-Setup-$version.exe") -Force

Write-Host "Приложение…"
if (-not (Test-Path (Join-Path $env:USERPROFILE ".couchpad\signing.properties"))) { throw "Нет ключа подписи: %USERPROFILE%\.couchpad\signing.properties" }
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = [Environment]::GetEnvironmentVariable("JAVA_HOME", "User") }
& (Join-Path $root "mobile\gradlew.bat") -p (Join-Path $root "mobile") :androidApp:assembleRelease -q
if ($LASTEXITCODE -ne 0) { throw "gradle: код $LASTEXITCODE" }
Copy-Item (Join-Path $root "mobile\androidApp\build\outputs\apk\release\androidApp-release.apk") (Join-Path $dist "Couchpad-$version.apk") -Force

Get-ChildItem $dist -File | Format-Table Name, @{ n = "МБ"; e = { [math]::Round($_.Length / 1MB, 1) } }
