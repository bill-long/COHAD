<#
.SYNOPSIS
  Run COHAD with in-memory data and dev JWT auth (no Cosmos, no Azure AD B2C).

.DESCRIPTION
  PowerShell equivalent of run-mock-data.sh. The API listens on HTTPS only
  (port 5001) so features that require a secure context work locally.

.EXAMPLE
  ./scripts/run-mock-data.ps1
  Print instructions (default).

.EXAMPLE
  ./scripts/run-mock-data.ps1 api
  Generate signing keys and start the API.
#>
[CmdletBinding()]
param(
    [ValidateSet('api')]
    [string]$Command
)

$ErrorActionPreference = 'Stop'

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$MockUrls = 'https://127.0.0.1:5001'

# 32 random bytes as hex (64 chars) — satisfies the >=32 UTF-8 byte HS256 requirement.
# Uses RandomNumberGenerator.Create().GetBytes(byte[]) + BitConverter so the script
# works on both Windows PowerShell 5.1 (.NET Framework) and PowerShell 7+ (.NET 5+);
# the static RandomNumberGenerator.GetBytes(int) and Convert.ToHexString are .NET 5+ only.
function New-SigningKey {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    } finally {
        $rng.Dispose()
    }
    [System.BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
}

if ($Command -eq 'api') {
    $env:MockJwt__SigningKey = New-SigningKey
    $env:UnsubscribeToken__SigningKey = New-SigningKey
    $env:ASPNETCORE_ENVIRONMENT = 'MockData'
    $env:ASPNETCORE_URLS = $MockUrls
    # --no-launch-profile so launchSettings.json applicationUrl does not override ASPNETCORE_URLS.
    & dotnet run --no-launch-profile --project (Join-Path $Root 'Web/Web.csproj')
    exit $LASTEXITCODE
}

# Portable across Windows PowerShell 5.1 and PowerShell 7+: a scriptblock generates a
# 64-char hex key via RandomNumberGenerator.Create().GetBytes(byte[]) + BitConverter.
$oneLiner = '$k={$r=[System.Security.Cryptography.RandomNumberGenerator]::Create();$b=New-Object byte[] 32;try{$r.GetBytes($b)}finally{$r.Dispose()};[BitConverter]::ToString($b).Replace("-","").ToLowerInvariant()}; ' +
            '$env:MockJwt__SigningKey=(& $k); ' +
            '$env:UnsubscribeToken__SigningKey=(& $k); ' +
            '$env:ASPNETCORE_ENVIRONMENT="MockData"; ' +
            "`$env:ASPNETCORE_URLS=`"$MockUrls`"; " +
            "dotnet run --no-launch-profile --project `"$Root/Web/Web.csproj`""

Write-Host "From repo root ($Root):"
Write-Host ""
Write-Host "  One-time - trust the ASP.NET Core dev HTTPS certificate (stops browser warnings):"
Write-Host "    dotnet dev-certs https --trust"
Write-Host ""
Write-Host "  Terminal 1 - Angular (mock auth):"
Write-Host "    cd Web/ClientApp; npm run start:mock"
Write-Host ""
Write-Host "  Terminal 2 - API (copy-paste; fresh random keys each run):"
Write-Host "    $oneLiner"
Write-Host ""
Write-Host "  Or from repo root, same effect as Terminal 2:"
Write-Host "    ./scripts/run-mock-data.ps1 api"
Write-Host ""
Write-Host "Open $MockUrls - signed in as mock@cohad.local (admin), with two seeded homes and taylor@cohad.local to manage."
Write-Host ""
Write-Host "Optional - email job mock simulation (see Web/appsettings.MockData.json EmailJobs:Mock):"
Write-Host "  `$env:EmailJobs__Mock__DelayMilliseconds=500"
Write-Host "  `$env:EmailJobs__Mock__RandomFailureProbability=0.3"
Write-Host "  `$env:EmailJobs__Mock__FailAllRecipients=`$true"
Write-Host "  `$env:EmailJobs__Mock__RandomFailSeed=42"
Write-Host "  `$env:EmailJobs__Mock__JobFatalError='Simulated fatal job error'"
