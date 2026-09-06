[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serviceName = 'OpenNetLimit'
$serviceExecutable = Join-Path $PSScriptRoot 'service\OpenNetLimit.Service.exe'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    Write-Error 'Open an Administrator PowerShell window, then run this script again.'
    exit 1
}

if (-not (Test-Path -LiteralPath $serviceExecutable -PathType Leaf)) {
    Write-Error "Service executable not found: $serviceExecutable"
    exit 1
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -eq $existing) {
    New-Service `
        -Name $serviceName `
        -BinaryPathName ('"{0}"' -f $serviceExecutable) `
        -DisplayName 'OpenNetLimit' `
        -Description 'Per-application bandwidth control and traffic monitoring.' `
        -StartupType Automatic | Out-Null
}
else {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }

    & sc.exe config $serviceName binPath= ('"{0}"' -f $serviceExecutable) start= auto | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not update the $serviceName service. sc.exe returned $LASTEXITCODE."
    }
}

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
Write-Host 'OpenNetLimit is installed and running.'
