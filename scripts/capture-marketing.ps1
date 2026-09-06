[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repoRoot 'OpenNetLimit.sln'
$captureProject = Join-Path $repoRoot 'tools\OpenNetLimit.MarketingCapture\OpenNetLimit.MarketingCapture.csproj'
$app = Join-Path $repoRoot 'src\OpenNetLimit.UI\bin\Release\net8.0-windows\OpenNetLimit.UI.exe'
$output = Join-Path $repoRoot 'assets\screenshots'

dotnet build $solution -c Release
if ($LASTEXITCODE -ne 0) { throw 'The release build failed.' }

dotnet run --project $captureProject -c Release --no-build -- --app $app --output $output
if ($LASTEXITCODE -ne 0) { throw 'The isolated screenshot capture failed.' }

Write-Host "Screenshots updated in $output"
