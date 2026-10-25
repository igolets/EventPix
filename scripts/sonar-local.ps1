# Runs the SonarQube Cloud analysis locally with the same flags as .github/workflows/sonarcloud.yml.
# Requires PowerShell 7+ and SONAR_TOKEN (sonarcloud.io -> My Account -> Security -> generate token).
# The quality-gate result appears on sonarcloud.io a few minutes after the run finishes.
#
# Usage:  $env:SONAR_TOKEN = "<token>";  pwsh scripts/sonar-local.ps1
$ErrorActionPreference = 'Stop'

if (-not $env:SONAR_TOKEN) {
    Write-Error "SONAR_TOKEN is not set. Generate one at https://sonarcloud.io/account/security and run: `$env:SONAR_TOKEN = '<token>'"
    exit 1
}

$scanner = Get-Command dotnet-sonarscanner -ErrorAction SilentlyContinue
if (-not $scanner) {
    Write-Host 'Installing dotnet-sonarscanner (one-time)...'
    dotnet tool install --global dotnet-sonarscanner
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $ext = if ($IsWindows) { '.exe' } else { '' }
    $scanner = Join-Path $HOME ".dotnet/tools/dotnet-sonarscanner$ext"
}
else {
    $scanner = $scanner.Source
}

$reports = Join-Path (Get-Location) 'TestResults'
if (Test-Path $reports) { Remove-Item $reports -Recurse -Force }

& $scanner begin `
    /k:"igolets_EventPix" /o:"igolets" /d:sonar.token="$env:SONAR_TOKEN" `
    "/d:sonar.cs.opencover.reportsPaths=$reports/**/coverage.opencover.xml" `
    '/d:sonar.exclusions=**/Migrations/**' `
    '/d:sonar.cpd.exclusions=**/themes/**/templates.html' `
    '/d:sonar.coverage.exclusions=**/wwwroot/**'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test --no-build --collect:'XPlat Code Coverage' --settings tests/coverlet.runsettings --results-directory $reports
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $scanner end /d:sonar.token="$env:SONAR_TOKEN"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Analysis uploaded. Quality-gate result in a few minutes at:'
Write-Host '  https://sonarcloud.io/summary/overall?id=igolets_EventPix'
