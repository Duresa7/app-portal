<#
.SYNOPSIS
    Installs the App Portal client for all users and writes its device configuration.

.DESCRIPTION
    Meant to run as SYSTEM from an Action1 (or any RMM) deployment, with the published client folder beside this script.
    The client goes to %ProgramFiles%\App Portal so that an AppLocker allow rule on Program Files covers it.
    The device token is written to %ProgramData%\AppPortal\client.json, readable by Users, writable only by administrators.

.PARAMETER ServerUrl
    Base URL of the App Portal server, for example http://portal.ad.example.com:8080

.PARAMETER DeviceToken
    Token printed by `AppPortal.Server device add`. Pass it from the RMM's secret parameter, not from a script file.

.PARAMETER Source
    Folder containing the published client (AppPortal.exe and its files). Defaults to a "client" folder beside this script.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ServerUrl,
    [Parameter(Mandatory)] [string] $DeviceToken,
    [string] $Source = (Join-Path $PSScriptRoot 'client'),
    [switch] $NoShortcut
)

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:ProgramFiles 'App Portal'
$configDir  = Join-Path $env:ProgramData 'AppPortal'
$configPath = Join-Path $configDir 'client.json'

if (-not (Test-Path (Join-Path $Source 'AppPortal.exe'))) {
    throw "AppPortal.exe not found under '$Source'."
}

# Program files
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Get-ChildItem -Path $installDir -Recurse -Force | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $Source '*') -Destination $installDir -Recurse -Force

# Configuration: users read, administrators write
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
$settings = [ordered]@{ serverUrl = $ServerUrl.TrimEnd('/'); deviceToken = $DeviceToken; refreshSeconds = 10 }
$settings | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
& icacls.exe $configDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' 'BUILTIN\Users:(OI)(CI)RX' | Out-Null

# Start menu shortcut for everyone
if (-not $NoShortcut) {
    $programs = [Environment]::GetFolderPath('CommonPrograms')
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $programs 'App Portal.lnk'))
    $shortcut.TargetPath = Join-Path $installDir 'AppPortal.exe'
    $shortcut.WorkingDirectory = $installDir
    $shortcut.IconLocation = Join-Path $installDir 'AppPortal.exe'
    $shortcut.Description = 'Install approved software on this computer'
    $shortcut.Save()
}

# Uninstall entry so the RMM inventory sees it
$reg = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AppPortalClient'
New-Item -Path $reg -Force | Out-Null
$version = (Get-Item (Join-Path $installDir 'AppPortal.exe')).VersionInfo.ProductVersion
Set-ItemProperty -Path $reg -Name DisplayName -Value 'App Portal'
Set-ItemProperty -Path $reg -Name DisplayVersion -Value $version
Set-ItemProperty -Path $reg -Name Publisher -Value 'Duresa Kadi'
Set-ItemProperty -Path $reg -Name InstallLocation -Value $installDir
Set-ItemProperty -Path $reg -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installDir\Uninstall-AppPortalClient.ps1`""
Set-ItemProperty -Path $reg -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $reg -Name NoRepair -Value 1 -Type DWord

Copy-Item -Path (Join-Path $PSScriptRoot 'Uninstall-AppPortalClient.ps1') -Destination $installDir -Force -ErrorAction SilentlyContinue

Write-Output "App Portal $version installed to '$installDir'; configuration at '$configPath'."
