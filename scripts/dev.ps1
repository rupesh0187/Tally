<#
.SYNOPSIS
  Runs TallyNext locally: PostgreSQL (Docker), the API (http://localhost:5080) and the web app (http://localhost:5090).
.DESCRIPTION
  The API applies database migrations and seeds a synthetic development company on first start
  (see src/TallyNext.Api/appsettings.Development.json for the development-only sign-in).
  Press Ctrl+C to stop; the API and web processes are stopped together.
.EXAMPLE
  ./scripts/dev.ps1
  ./scripts/dev.ps1 -ApiOnly
#>
[CmdletBinding()]
param(
    [switch]$ApiOnly,
    [switch]$SkipDatabase
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $SkipDatabase) {
    docker compose up -d | Out-Host
    $deadline = (Get-Date).AddMinutes(3)
    do {
        Start-Sleep -Seconds 2
        $health = docker inspect --format '{{.State.Health.Status}}' tallynext-postgres-1 2>$null
    } while ($health -ne 'healthy' -and (Get-Date) -lt $deadline)
    if ($health -ne 'healthy') { throw 'PostgreSQL did not become healthy. See: docker compose logs postgres' }
}

dotnet build TallyNext.Core.slnf -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$api = Start-Process dotnet -ArgumentList 'run --no-build --project src/TallyNext.Api --launch-profile http' -PassThru -NoNewWindow
$deadline = (Get-Date).AddMinutes(2)
do {
    Start-Sleep -Seconds 2
    try { $ok = (Invoke-WebRequest -UseBasicParsing http://localhost:5080/health -TimeoutSec 2).StatusCode -eq 200 } catch { $ok = $false }
} while (-not $ok -and (Get-Date) -lt $deadline -and -not $api.HasExited)
if (-not $ok) { Stop-Process -Id $api.Id -ErrorAction SilentlyContinue; throw 'The API did not start.' }
Write-Host 'API:     http://localhost:5080  (Swagger UI: http://localhost:5080/swagger)' -ForegroundColor Green

$web = $null
if (-not $ApiOnly) {
    $web = Start-Process dotnet -ArgumentList 'run --no-build --project src/TallyNext.Web --launch-profile http' -PassThru -NoNewWindow
    Write-Host 'Web app: http://localhost:5090' -ForegroundColor Green
}

try {
    Wait-Process -Id $api.Id
}
finally {
    foreach ($p in @($web, $api)) {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -ErrorAction SilentlyContinue }
    }
}
