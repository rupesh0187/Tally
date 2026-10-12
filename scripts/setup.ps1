<#
.SYNOPSIS
  Checks prerequisites and prepares a development machine for TallyNext.
.DESCRIPTION
  - Verifies the .NET 10 SDK, the MAUI Windows workload and Docker.
  - Restores the repo-local dotnet tools (dotnet-ef) and NuGet packages.
  - Optionally starts the development PostgreSQL container (-StartDatabase).
  Makes no system-wide changes: missing components are reported with the command to install them.
.EXAMPLE
  ./scripts/setup.ps1 -StartDatabase
#>
[CmdletBinding()]
param(
    [switch]$StartDatabase
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Write-Check([string]$name, [bool]$ok, [string]$hint) {
    if ($ok) { Write-Host "[ ok ] $name" -ForegroundColor Green }
    else { Write-Host "[warn] $name -> $hint" -ForegroundColor Yellow }
}

$sdks = (& dotnet --list-sdks) -join "`n"
Write-Check '.NET 10 SDK' ($sdks -match '^10\.' -or $sdks -match "`n10\.") 'Install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10'

$workloads = (& dotnet workload list) -join "`n"
$mauiPack = Test-Path (Join-Path (Split-Path (Get-Command dotnet).Source) 'packs\Microsoft.Maui.Sdk.net10')
Write-Check 'MAUI Windows workload (desktop app only)' ($workloads -match 'maui-windows' -and $mauiPack) `
    'In an elevated PowerShell run: dotnet workload install maui-windows   (or Visual Studio Installer > .NET Multi-platform App UI development)'

$docker = $null -ne (Get-Command docker -ErrorAction SilentlyContinue)
Write-Check 'Docker (development PostgreSQL)' $docker 'Install Docker Desktop, or point ConnectionStrings__Server at an existing PostgreSQL 16+'

Write-Host 'Restoring local tools and packages...'
dotnet tool restore | Out-Host
dotnet restore TallyNext.Core.slnf | Out-Host

if ($StartDatabase) {
    if (-not $docker) { throw 'Docker is not available.' }
    docker compose up -d | Out-Host
    Write-Host 'Waiting for PostgreSQL to become healthy...'
    $deadline = (Get-Date).AddMinutes(3)
    do {
        Start-Sleep -Seconds 2
        $health = docker inspect --format '{{.State.Health.Status}}' tallynext-postgres-1 2>$null
    } while ($health -ne 'healthy' -and (Get-Date) -lt $deadline)
    Write-Check 'PostgreSQL container' ($health -eq 'healthy') 'Check: docker compose logs postgres'
}

Write-Host "`nNext: ./scripts/dev.ps1 to run the API and web app, ./scripts/test.ps1 to run the tests."
