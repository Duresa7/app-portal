[CmdletBinding()]
param()
$ErrorActionPreference = 'SilentlyContinue'
Get-Process -Name AppPortal | Stop-Process -Force
Remove-Item -Path (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'App Portal.lnk') -Force
Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AppPortalClient' -Recurse -Force
Remove-Item -Path (Join-Path $env:ProgramData 'AppPortal') -Recurse -Force
Remove-Item -Path (Join-Path $env:ProgramFiles 'App Portal') -Recurse -Force
Write-Output 'App Portal removed.'
