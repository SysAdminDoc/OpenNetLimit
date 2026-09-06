[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$expectedPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

if (-not $artifactRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean an artifact path outside the repository: $artifactRoot"
}

[xml]$buildProperties = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'Directory.Build.props')
$version = [string]$buildProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Directory.Build.props does not contain a project version.'
}

if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}

$stagingRoot = Join-Path $artifactRoot 'staging'
$packageName = "OpenNetLimit-v$version-$Runtime"
$packageRoot = Join-Path $artifactRoot $packageName
$serviceRoot = Join-Path $packageRoot 'service'

New-Item -ItemType Directory -Path $stagingRoot, $packageRoot, $serviceRoot -Force | Out-Null

dotnet publish (Join-Path $repoRoot 'src\OpenNetLimit.UI\OpenNetLimit.UI.csproj') `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o (Join-Path $stagingRoot 'ui')
if ($LASTEXITCODE -ne 0) { throw 'The UI publish failed.' }

dotnet publish (Join-Path $repoRoot 'src\OpenNetLimit.CLI\OpenNetLimit.CLI.csproj') `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o (Join-Path $stagingRoot 'cli')
if ($LASTEXITCODE -ne 0) { throw 'The CLI publish failed.' }

dotnet publish (Join-Path $repoRoot 'src\OpenNetLimit.Service\OpenNetLimit.Service.csproj') `
    -c Release -r $Runtime --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $serviceRoot
if ($LASTEXITCODE -ne 0) { throw 'The service publish failed.' }

Copy-Item -LiteralPath (Join-Path $stagingRoot 'ui\OpenNetLimit.UI.exe') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $stagingRoot 'cli\onl.exe') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\Install-Service.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\Uninstall-Service.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'assets') -Destination (Join-Path $packageRoot 'assets') -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination (Join-Path $packageRoot 'docs') -Recurse

$zipPath = Join-Path $artifactRoot "$packageName.zip"
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash
$checksumPath = "$zipPath.sha256"
[IO.File]::WriteAllText($checksumPath, "$hash  $packageName.zip`r`n")

Remove-Item -LiteralPath $stagingRoot -Recurse -Force

Write-Host "Release archive: $zipPath"
Write-Host "SHA-256: $hash"
