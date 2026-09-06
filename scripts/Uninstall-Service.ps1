[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serviceName = 'OpenNetLimit'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    Write-Error 'Open an Administrator PowerShell window, then run this script again.'
    exit 1
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -eq $existing) {
    Write-Host 'OpenNetLimit is not installed.'
    exit 0
}

if ($existing.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
}

& sc.exe delete $serviceName | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not remove the $serviceName service. sc.exe returned $LASTEXITCODE."
}

Write-Host 'OpenNetLimit was removed. Rules and traffic history were kept.'
