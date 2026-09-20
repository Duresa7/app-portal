[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [string] $Version
)
$ErrorActionPreference = 'Stop'
$msi = (Resolve-Path $Path).Path
$installDir = Join-Path $env:ProgramFiles 'App Portal'
$dataDir = Join-Path $env:ProgramData 'AppPortal'
$logDir = Join-Path $env:RUNNER_TEMP 'app-portal-msi-logs'
New-Item -ItemType Directory -Force $logDir | Out-Null

function Invoke-Msi([string] $Operation, [string] $Name, [string[]] $Properties = @()) {
    $log = Join-Path $logDir "$Name.log"
    $arguments = @($Operation, "`"$msi`"", '/qn', '/norestart', '/l*v', "`"$log`"") + $Properties
    $process = Start-Process msiexec.exe -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -notin 0, 3010) { throw "msiexec $Name exited $($process.ExitCode); see $log" }
    if (Select-String -Path $log -SimpleMatch 'ape_ci_not_a_real_key' -Quiet) {
        throw "The enrollment key appeared in $Name's MSI log."
    }
}

# No enrollment server is needed to prove that Windows Installer hands the private file to SYSTEM.
Invoke-Msi '/i' 'install' @('SERVERURL=http://127.0.0.1:59999', 'ENROLLMENTKEY=ape_ci_not_a_real_key')
$service = Get-Service AppPortalAgent
$service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
$configuration = Get-CimInstance Win32_Service -Filter "Name='AppPortalAgent'"
if ($configuration.StartName -ne 'LocalSystem') { throw 'The agent does not run as SYSTEM.' }
foreach ($file in 'AppPortal.exe', 'AppPortal.Agent.exe') {
    if (-not (Test-Path (Join-Path $installDir $file))) { throw "$file was not installed." }
}
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'App Portal.lnk'
if (-not (Test-Path $shortcut)) { throw 'The all-users shortcut was not installed.' }
$entry = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' |
    Where-Object DisplayName -eq 'App Portal'
if ($entry.DisplayVersion -ne $Version) { throw 'ARP does not report the package version.' }
$enrollmentPath = Join-Path $dataDir 'enroll.json'
$enrollment = Get-Content $enrollmentPath -Raw | ConvertFrom-Json
if ($enrollment.serverUrl -ne 'http://127.0.0.1:59999' -or $enrollment.enrollmentKey -ne 'ape_ci_not_a_real_key' -or $null -ne $enrollment.action1EndpointId) {
    throw 'The enrollment file does not match the MSI properties.'
}
$acl = Get-Acl $enrollmentPath
if (-not $acl.AreAccessRulesProtected) { throw 'The enrollment file inherits user access.' }
foreach ($rule in $acl.Access) {
    $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
    if ($rule.AccessControlType -eq 'Allow' -and $sid -notin 'S-1-5-18', 'S-1-5-32-544') {
        throw "Unexpected access to the enrollment key: $sid"
    }
}
Set-Content (Join-Path $dataDir 'retained.txt') 'Preserve data across uninstall.'
Invoke-Msi '/x' 'uninstall'
if (Get-Service AppPortalAgent -ErrorAction SilentlyContinue) { throw 'The service survived uninstall.' }
if (Test-Path $installDir) { throw 'Program Files payload survived uninstall.' }
if (Test-Path $shortcut) { throw 'The Start menu shortcut survived uninstall.' }
if (-not (Test-Path (Join-Path $dataDir 'retained.txt'))) { throw 'Uninstall removed retained data.' }

Invoke-Msi '/i' 'install-endpoint' @('SERVERURL=http://127.0.0.1:59999', 'ENROLLMENTKEY=ape_ci_not_a_real_key', 'ACTION1ENDPOINTID=endpoint-ci')
$enrollment = Get-Content $enrollmentPath -Raw | ConvertFrom-Json
if ($enrollment.action1EndpointId -ne 'endpoint-ci') { throw 'The optional endpoint was not written.' }
Invoke-Msi '/x' 'remove-data' @('REMOVEDATA=1')
if (Test-Path $dataDir) { throw 'REMOVEDATA=1 left data behind.' }
'Windows MSI smoke checks passed.'
