<#
.SYNOPSIS
    Publica Lemon y lo deja "instalado": cierra la versión abierta, publica en Release,
    crea el acceso directo en el menú Inicio y vuelve a abrir la app.

.EXAMPLE
    .\publish.ps1                      # publica en C:\Tools\Lemon
    .\publish.ps1 -Autostart           # además activa "Iniciar con Windows"
    .\publish.ps1 -SelfContained       # incluye .NET (no requiere tenerlo instalado; ~150 MB)
    .\publish.ps1 -InstallDir D:\Apps\Lemon

    Si PowerShell bloquea el script:
    powershell -ExecutionPolicy Bypass -File .\publish.ps1
#>
param(
    [string]$InstallDir = "C:\Tools\Lemon",
    [switch]$SelfContained,
    [switch]$Autostart,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$ProjectDir = $PSScriptRoot
$Exe = Join-Path $InstallDir "Lemon.exe"
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

function Step($text) { Write-Host "`n▸ $text" -ForegroundColor Yellow }

# ── 0. Comprobaciones ─────────────────────────────────────────────
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "No se encontró 'dotnet'. Instala el SDK: winget install Microsoft.DotNet.SDK.8"
}
if (-not (Test-Path (Join-Path $ProjectDir "Lemon.csproj"))) {
    throw "Ejecuta este script desde la carpeta del proyecto (no encontré Lemon.csproj)."
}
# Por seguridad solo vaciamos la carpeta de instalación si está vacía o ya contiene Lemon.
if ((Test-Path $InstallDir) -and (Get-ChildItem $InstallDir -Force | Select-Object -First 1) -and -not (Test-Path $Exe)) {
    throw "La carpeta '$InstallDir' tiene otros archivos y no parece una instalación de Lemon. Usa -InstallDir con otra ruta."
}

# ── 1. Cerrar la versión en ejecución ─────────────────────────────
Step "Cerrando Lemon si está abierto"
$running = Get-Process -Name "Lemon" -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    Write-Host "  Cerrado."
} else {
    Write-Host "  No estaba abierto."
}

# ── 2. Publicar ───────────────────────────────────────────────────
Step "Publicando en $InstallDir"
if (Test-Path $InstallDir) { Remove-Item (Join-Path $InstallDir "*") -Recurse -Force }

$publishArgs = @(
    "publish", (Join-Path $ProjectDir "Lemon.csproj"),
    "-c", "Release",
    "-r", "win-x64",
    "-o", $InstallDir,
    "-p:PublishSingleFile=true",
    "-p:DebugType=none"
)
if ($SelfContained) {
    $publishArgs += @("--self-contained", "true", "-p:IncludeNativeLibrariesForSelfExtract=true")
} else {
    $publishArgs += @("--self-contained", "false")
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló (código $LASTEXITCODE)." }
if (-not (Test-Path $Exe)) { throw "La publicación terminó pero no encontré $Exe." }

# ── 3. Acceso directo en el menú Inicio ───────────────────────────
Step "Creando acceso directo en el menú Inicio"
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Programs")) "Lemon.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $Exe
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = "Lemon: convierte archivos arrastrándolos y tocando Shift o Ctrl"
$shortcut.Save()
Write-Host "  $shortcutPath"

# ── 4. Iniciar con Windows ────────────────────────────────────────
# Mismo formato que escribe la app (Services/Autostart.cs), para que el menú de la bandeja lo reconozca.
$runCommand = "`"$Exe`" --background"
$current = (Get-ItemProperty -Path $RunKey -Name "Lemon" -ErrorAction SilentlyContinue).Lemon
if ($Autostart -or $current) {
    Step "Configurando inicio con Windows"
    Set-ItemProperty -Path $RunKey -Name "Lemon" -Value $runCommand
    Write-Host "  $runCommand"
}
# Entrada de la versión anterior (TangerineWin)
Remove-ItemProperty -Path $RunKey -Name "TangerineWin" -ErrorAction SilentlyContinue

# ── 5. Abrir ──────────────────────────────────────────────────────
if (-not $NoLaunch) {
    Step "Abriendo Lemon"
    # Se lanza a través del Explorador para que NO herede permisos de administrador
    # si esta terminal es elevada (el Explorador no puede soltar archivos en apps elevadas).
    Start-Process explorer.exe -ArgumentList "`"$Exe`""
}

Write-Host "`n✔ Listo. Lemon está en $InstallDir" -ForegroundColor Green
if (-not $SelfContained) {
    Write-Host "  (Requiere el runtime de .NET 8 Desktop; usa -SelfContained si lo copias a otra PC.)" -ForegroundColor DarkGray
}
