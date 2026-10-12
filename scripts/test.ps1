<#
.SYNOPSIS
  Builds and runs the automated tests.
.DESCRIPTION
  By default the server side of integration and sync tests runs on throw-away SQLite databases.
  -Postgres runs them against PostgreSQL instead (each test server creates and drops its own database);
  it uses the docker-compose database unless -PostgresAdminConnection is given.
.EXAMPLE
  ./scripts/test.ps1
  ./scripts/test.ps1 -Postgres
#>
[CmdletBinding()]
param(
    [switch]$Postgres,
    [string]$PostgresAdminConnection = 'Host=localhost;Port=5433;Database=postgres;Username=tallynext;Password=tallynext_dev_only'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if ($Postgres) {
    $env:TALLYNEXT_TEST_POSTGRES = $PostgresAdminConnection
    Write-Host 'Server-side tests use PostgreSQL.' -ForegroundColor Cyan
}
else {
    $env:TALLYNEXT_TEST_POSTGRES = $null
    Write-Host 'Server-side tests use SQLite (pass -Postgres for PostgreSQL).' -ForegroundColor Cyan
}

dotnet build TallyNext.Core.slnf -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$failed = $false
foreach ($project in 'tests/TallyNext.UnitTests', 'tests/TallyNext.IntegrationTests', 'tests/TallyNext.SyncTests') {
    Write-Host "`n=== $project" -ForegroundColor Cyan
    dotnet test --project $project --no-build
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}

if ($failed) { throw 'Some tests failed.' }
Write-Host "`nAll tests passed." -ForegroundColor Green
