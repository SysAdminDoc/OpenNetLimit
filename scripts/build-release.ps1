[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    # Leaves out the installer, for a PC without Inno Setup. Releases include it.
    [switch]$SkipSetup
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$expectedPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$heroProject = Join-Path $repoRoot 'tools\OpenNetLimit.MarketingHero\OpenNetLimit.MarketingHero.csproj'

if (-not $artifactRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean an artifact path outside the repository: $artifactRoot"
}

[xml]$buildProperties = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'Directory.Build.props')
$version = [string]$buildProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Directory.Build.props does not contain a project version.'
}

# Found before anything is built, so a missing compiler fails in seconds, not after the publishes.
$isccPath = $null
if (-not $SkipSetup) {
    $isccCandidates = @(
        (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $isccPath = $isccCandidates | Select-Object -First 1
    if (-not $isccPath) {
        throw 'Inno Setup (ISCC.exe) was not found. Install Inno Setup 6.3 or newer, or pass -SkipSetup to build only the ZIP.'
    }
}

dotnet run --project $heroProject -c Release -- --repo $repoRoot --select 2
if ($LASTEXITCODE -ne 0) { throw 'The README hero render failed.' }

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

function Write-Checksum([string]$path) {
    $fileHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    [IO.File]::WriteAllText("$path.sha256", "$fileHash  $([IO.Path]::GetFileName($path))`r`n")
    Write-Host "$path"
    Write-Host "SHA-256: $fileHash"
}

if ($isccPath) {
    & $isccPath /Q "/DAppVersion=$version" "/DSourceDir=$packageRoot" "/DOutputDir=$artifactRoot" "/DRuntime=$Runtime" `
        (Join-Path $repoRoot 'installer\OpenNetLimit.iss')
    if ($LASTEXITCODE -ne 0) { throw 'The installer build failed.' }
}

$zipPath = Join-Path $artifactRoot "$packageName.zip"
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal

Remove-Item -LiteralPath $stagingRoot -Recurse -Force

Write-Checksum $zipPath
if ($isccPath) { Write-Checksum (Join-Path $artifactRoot "OpenNetLimit-v$version-setup-$Runtime.exe") }
