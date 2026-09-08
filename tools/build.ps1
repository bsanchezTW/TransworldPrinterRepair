<#
.SYNOPSIS
    Genera TransworldPrinterRepair.exe: portable, autocontenido y de un solo archivo.

.DESCRIPTION
    1. Copia los paquetes de driver (.zip) al proyecto para que se embeban en el binario.
    2. Publica win-x64 self-contained y single-file.
    3. Deja el ejecutable en publish\ listo para copiar a cualquier PC.

    El equipo destino NO necesita .NET, PowerShell adicional ni VC++ Redistributable.

.PARAMETER DriversSource
    Carpeta que contiene los .zip de los controladores.

.PARAMETER SkipDriverSync
    Usa los .zip que ya estan en Resources\Drivers sin volver a copiarlos.

.EXAMPLE
    .\tools\build.ps1
#>
[CmdletBinding()]
param(
    [string] $DriversSource = "$env:USERPROFILE\OneDrive - TW_P&T\Documentos\Impresoras\Controladores de Impresoras",
    [switch] $SkipDriverSync
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $root 'src\TransworldPrinterRepair\TransworldPrinterRepair.csproj'
$driversDir = Join-Path $root 'src\TransworldPrinterRepair\Resources\Drivers'
$outputDir  = Join-Path $root 'publish'

Write-Host ''
Write-Host 'AutoReparacion de Impresoras Transworld - publicacion' -ForegroundColor Cyan
Write-Host ('-' * 60)

# ---- 1. Controladores -------------------------------------------------

if (-not $SkipDriverSync) {
    if (-not (Test-Path -LiteralPath $DriversSource)) {
        throw "No se encontro la carpeta de controladores: $DriversSource`nUsa -DriversSource para indicar la ruta correcta, o -SkipDriverSync si ya estan copiados."
    }

    New-Item -ItemType Directory -Force -Path $driversDir | Out-Null

    $zips = Get-ChildItem -LiteralPath $DriversSource -Filter *.zip
    if ($zips.Count -eq 0) { throw "No hay ningun .zip en $DriversSource" }

    foreach ($zip in $zips) {
        Copy-Item -LiteralPath $zip.FullName -Destination $driversDir -Force
        Write-Host ("  driver  {0,-40} {1,6:N1} MB" -f $zip.Name, ($zip.Length / 1MB))
    }
}

$embedded = Get-ChildItem -LiteralPath $driversDir -Filter *.zip -ErrorAction SilentlyContinue
if (-not $embedded) { throw "No hay controladores en $driversDir. El ejecutable no serviria de nada." }

$totalMb = [math]::Round((($embedded | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "  $($embedded.Count) paquetes, $totalMb MB a embeber en el ejecutable."
Write-Host ''

# ---- 2. Publicacion ---------------------------------------------------

if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }

Write-Host 'Publicando (tarda varios minutos por el tamano de los controladores)...' -ForegroundColor Yellow

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $outputDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "La publicacion fallo con codigo $LASTEXITCODE." }

# ---- 3. Resultado -----------------------------------------------------

$exe = Join-Path $outputDir 'TransworldPrinterRepair.exe'
if (-not (Test-Path $exe)) { throw "No se genero $exe" }

# Sobran los .pdb: el ejecutable ya es autocontenido.
Get-ChildItem $outputDir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host ''
Write-Host ('-' * 60)
Write-Host 'Listo.' -ForegroundColor Green
Write-Host ("  {0}" -f $exe)
Write-Host ("  {0:N1} MB" -f ((Get-Item $exe).Length / 1MB))
Write-Host ''
Write-Host 'Copia ese unico archivo al PC del trabajador y ejecutalo. No requiere instalacion.'
Write-Host 'Para diagnosticar un equipo:  TransworldPrinterRepair.exe --selftest'
Write-Host ''
