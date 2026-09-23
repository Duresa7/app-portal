<#
.SYNOPSIS
    Proves that the MSI and the bootstrapper install, enroll, work, and come off again.

.DESCRIPTION
    The release gate runs this on a Windows runner against a real server started in the job, so a
    broken installer cannot ship. It is a plain script rather than inline workflow steps so that the
    same checks can be run by hand on a virtual machine when something only reproduces there.

    The script installs and uninstalls software and writes to %ProgramData%. Run it on a throwaway
    machine, never on a workstation that has App Portal on it for real.

    Order matters. Static checks come first because they need no install. The wizard path follows on a
    clean machine, because that is what a technician meets. The upgrade check comes last, because it
    has to leave a previous version installed to have anything to upgrade.

.PARAMETER Msi
    The freshly built AppPortal-<version>-x64.msi.

.PARAMETER Version
    The version Directory.Build.props declares. Everything the package reports must equal it.

.PARAMETER Setup
    AppPortalSetup.exe. When it is absent the script installs the MSI directly with the same
    properties, so the checks still run before the bootstrapper exists.

.PARAMETER PreviousMsi
    A released MSI to install before the new one, for the upgrade check. Skipped when not supplied.

.PARAMETER ServerDll
    A built AppPortal.Server.dll. Built from source when not supplied.

.PARAMETER Port
    The loopback port the fake-mode server listens on.

.PARAMETER LogDirectory
    Where msiexec logs, server logs and the screenshot are written.

.PARAMETER Signing
    What signature the build promises, checked by Test-Signatures.ps1 on the MSI and the bootstrapper
    before anything is installed, and on the App Portal files in %ProgramFiles%\App Portal afterwards,
    which proves the MSI carries the signed payload. Test expects a test certificate, Release a valid
    SignPath Foundation signature with a timestamp. Off only reports, so the script still runs by hand
    on any machine against an unsigned build.

.EXAMPLE
    ./deploy/windows/ci-installer-test.ps1 -Msi out/installer/AppPortal-0.9.0-x64.msi -Version 0.9.0 -Setup out/setup/AppPortalSetup.exe -Signing Off
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Msi,
    [Parameter(Mandatory)] [string] $Version,
    [string] $Setup,
    [string] $PreviousMsi,
    [string] $ServerDll,
    [int] $Port = 5080,
    [string] $LogDirectory,
    [ValidateSet('Off','Test','Release')] [string] $Signing = 'Off'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The identity Windows Installer uses to find the previous package. It must never change, so the test
# asserts it rather than reading it from the build.
$ExpectedUpgradeCode = '{5AAB338F-4FEA-48AA-9931-826DC33F536A}'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$msiPath = (Resolve-Path $Msi).Path
$serverUrl = "http://127.0.0.1:$Port"
$installDir = Join-Path $env:ProgramFiles 'App Portal'
$dataDir = Join-Path $env:ProgramData 'AppPortal'
$settingsPath = Join-Path $dataDir 'client.json'
$adminPassword = 'ci-Admin-' + [Guid]::NewGuid().ToString('N')

if (-not $LogDirectory) {
    $LogDirectory = Join-Path ($env:RUNNER_TEMP ?? $env:TEMP) 'app-portal-installer-test'
}
New-Item -ItemType Directory -Force $LogDirectory | Out-Null

# Write-Step, Wait-For and Invoke-Msi, shared with Test-RealPc.ps1.
Import-Module (Join-Path $PSScriptRoot 'InstallerTestHelpers.psm1') -Force
$PSDefaultParameterValues['Invoke-Msi:LogDirectory'] = $LogDirectory

$resetScript = Join-Path $PSScriptRoot 'Reset-AppPortal.ps1'
$signatureScript = Join-Path $PSScriptRoot 'Test-Signatures.ps1'

# ---------------------------------------------------------------------------------------------
# Static checks. A package that names the wrong version or the wrong upgrade code either refuses to
# upgrade the release before it or silently installs beside it, and neither is visible until a fleet
# has it.
# ---------------------------------------------------------------------------------------------
Write-Step 'MSI static checks'

function Read-MsiTable {
    param([Parameter(Mandatory)] [string] $Package, [Parameter(Mandatory)] [string] $Query)

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Package, 0))
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($Query))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
    $rows = @()
    while ($true) {
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) { break }
        $rows += $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
    }
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null

    # Every handle has to go before msiexec can install the file we just read, and a collection only
    # helps once nothing in this scope still points at the database.
    foreach ($handle in $view, $database, $installer) {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($handle) | Out-Null
    }
    $record = $null; $view = $null; $database = $null; $installer = $null
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    return $rows
}

$productVersion = (Read-MsiTable $msiPath "SELECT Value FROM Property WHERE Property='ProductVersion'") |
    Select-Object -First 1
if ($productVersion -ne $Version) {
    throw "The MSI reports ProductVersion $productVersion, expected $Version."
}

$upgradeCode = (Read-MsiTable $msiPath "SELECT Value FROM Property WHERE Property='UpgradeCode'") |
    Select-Object -First 1
if ($upgradeCode -ne $ExpectedUpgradeCode) {
    throw "The MSI reports UpgradeCode $upgradeCode, expected $ExpectedUpgradeCode. Changing it orphans every installed copy."
}

# The File table stores "short|long"; the long name is what lands on disk.
$packaged = Read-MsiTable $msiPath 'SELECT FileName FROM File' | ForEach-Object { ($_ -split '\|')[-1] }
foreach ($required in 'AppPortal.exe', 'AppPortal.Agent.exe') {
    if ($packaged -notcontains $required) {
        throw "$required is not in the MSI. The package has $($packaged.Count) files."
    }
}
"ProductVersion $productVersion, UpgradeCode $upgradeCode, $($packaged.Count) files packaged."

# ---------------------------------------------------------------------------------------------
# Before anything is installed: a package that promises a signature and lacks one would be refused
# by every signed agent that downloads it.
# ---------------------------------------------------------------------------------------------
Write-Step "The packages carry their signatures ($Signing)"

$packages = @($msiPath)
if ($Setup) { $packages += (Resolve-Path $Setup).Path }
& $signatureScript -Mode $Signing -Path $packages

# ---------------------------------------------------------------------------------------------
# A runner that keeps its disk starts where the last run stopped. On windows-latest that could not
# happen, so nothing here looked for it; a self-hosted runner makes it the ordinary case. The release
# gate also runs this script before Verify-Msi.ps1, which fails differently on the same leftovers.
# ---------------------------------------------------------------------------------------------
Write-Step 'Leave nothing of an earlier run in place'

& $resetScript
'What follows is this run and not the last one.'

# ---------------------------------------------------------------------------------------------
# A real server, in fake Action1 mode, on loopback. The Windows runner cannot run Linux containers,
# so this is the server process from the build output rather than the image.
# ---------------------------------------------------------------------------------------------
Write-Step 'Start the server in fake Action1 mode'

if (-not $ServerDll) {
    Push-Location $repositoryRoot
    try {
        dotnet build src/AppPortal.Server -c Release
        if ($LASTEXITCODE -ne 0) { throw 'The server build failed.' }
    }
    finally { Pop-Location }
    $ServerDll = Join-Path $repositoryRoot 'src/AppPortal.Server/bin/Release/net10.0/AppPortal.Server.dll'
}
$ServerDll = (Resolve-Path $ServerDll).Path

$env:Action1__Mode = 'Fake'
$env:Portal__DataDirectory = Join-Path $LogDirectory 'server-data'
$env:ASPNETCORE_URLS = $serverUrl
# The agent and the client must read the installed client.json, not a developer override left in the
# environment by an earlier step.
Remove-Item Env:APPPORTAL_CONFIG, Env:APPPORTAL_SERVER_URL, Env:APPPORTAL_DEVICE_TOKEN -ErrorAction SilentlyContinue

$env:APPPORTAL_ADMIN_PASSWORD = $adminPassword
dotnet $ServerDll admin add --username ci-admin
if ($LASTEXITCODE -ne 0) { throw 'Creating the administrator failed.' }

$keyOutput = dotnet $ServerDll key create --name ci-installer --engine agent
if ($LASTEXITCODE -ne 0) { throw 'Creating the enrollment key failed.' }
$enrollmentKey = $keyOutput | Where-Object { $_ -match '^ape_' } | Select-Object -Last 1
if (-not $enrollmentKey) { throw "No enrollment key was printed. Output was: $($keyOutput -join ' / ')" }

# One app from a package manager, so the managed executor runs on a real PC before the tag rather than
# after it. Windows PowerShell 5.1 is the one manager every Windows PC already has, and a small module
# from the PowerShell Gallery is quick to install and leaves nothing behind once removed.
$managedModule = 'powershell-yaml'
$managedModulePath = Join-Path $env:ProgramFiles "WindowsPowerShell\Modules\$managedModule"
# A runner that keeps its disk may still carry it from a run that failed half way.
Remove-Item $managedModulePath -Recurse -Force -ErrorAction SilentlyContinue
$catalogFile = Join-Path $LogDirectory 'ci-catalog.json'
@{
    apps = @(@{
        id = 'ci-managed'
        name = 'CI managed package'
        userRemovable = $true
        agent = [ordered]@{ kind = 'managed'; manager = 'powershell5-module'; id = $managedModule; scope = 'machine' }
    })
} | ConvertTo-Json -Depth 5 | Set-Content -Path $catalogFile -Encoding utf8
dotnet $ServerDll catalog import $catalogFile
if ($LASTEXITCODE -ne 0) { throw 'Importing the CI catalog failed.' }

$server = Start-Process dotnet -ArgumentList "`"$ServerDll`"" -PassThru `
    -RedirectStandardOutput (Join-Path $LogDirectory 'server.log') `
    -RedirectStandardError (Join-Path $LogDirectory 'server-error.log')

try {
    Wait-For -Description 'the server to answer /healthz' -Condition {
        $null -ne (Invoke-RestMethod "$serverUrl/healthz" -TimeoutSec 2)
    }
    "Server is up on $serverUrl."

    # -----------------------------------------------------------------------------------------
    # The path a technician takes: one executable, two answers, a working PC.
    # -----------------------------------------------------------------------------------------
    if ($Setup) {
        Write-Step 'Silent bootstrapper install'
        $setupPath = (Resolve-Path $Setup).Path
        $process = Start-Process $setupPath -Wait -PassThru `
            -ArgumentList '/quiet', '/server', $serverUrl, '/key', $enrollmentKey
        if ($process.ExitCode -ne 0) {
            throw "AppPortalSetup.exe exited $($process.ExitCode), expected 0. Logs are in $LogDirectory."
        }
        'The bootstrapper returned 0.'
    }
    else {
        Write-Step 'Silent MSI install (no bootstrapper supplied)'
        Invoke-Msi -Operation '/i' -Name 'install' -Package $msiPath `
            -Properties @("SERVERURL=$serverUrl", "ENROLLMENTKEY=$enrollmentKey") | Out-Null
    }

    Write-Step 'The agent service is running as SYSTEM'
    $service = Get-Service AppPortalAgent
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    $configuration = Get-CimInstance Win32_Service -Filter "Name='AppPortalAgent'"
    if ($configuration.StartName -ne 'LocalSystem') { throw 'The agent does not run as SYSTEM.' }
    'AppPortalAgent is running as LocalSystem.'

    Write-Step 'The device enrolled and holds a token'
    Wait-For -Description 'client.json to carry a device token' -Seconds 120 -Condition {
        (Test-Path $settingsPath) -and
        -not [string]::IsNullOrWhiteSpace((Get-Content $settingsPath -Raw | ConvertFrom-Json).deviceToken)
    }
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    if ($settings.deviceToken -notmatch '^apd_') { throw 'The stored device token is not a device token.' }
    if ($settings.serverUrl -ne $serverUrl) { throw "client.json points at $($settings.serverUrl), expected $serverUrl." }
    if (Test-Path (Join-Path $dataDir 'enroll.json')) { throw 'The enrollment key file survived enrollment.' }
    'client.json holds a device token and the enrollment key file is gone.'

    Write-Step 'An administrator sees the device with a heartbeat'
    # Signing in for real proves the web admin path too, not only the database row.
    $login = Invoke-WebRequest "$serverUrl/admin/login" -SessionVariable adminSession -TimeoutSec 10
    $token = [regex]::Match($login.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    if (-not $token) { throw 'The sign-in page carried no antiforgery token.' }
    Invoke-WebRequest "$serverUrl/admin/login" -Method Post -WebSession $adminSession -TimeoutSec 10 `
        -Body @{ Username = 'ci-admin'; Password = $adminPassword; __RequestVerificationToken = $token } | Out-Null

    $deviceName = $env:COMPUTERNAME
    Wait-For -Description "$deviceName to appear on /admin/devices with an agent version" -Seconds 120 -Condition {
        $page = Invoke-WebRequest "$serverUrl/admin/devices" -WebSession $adminSession -TimeoutSec 10
        $page.Content -match [regex]::Escape($deviceName) -and $page.Content -match [regex]::Escape($Version)
    }
    $devices = (Invoke-WebRequest "$serverUrl/admin/devices" -WebSession $adminSession -TimeoutSec 10).Content
    $row = [regex]::Match($devices, "<tr[^>]*>(?:(?!</tr>).)*$([regex]::Escape($deviceName))(?:(?!</tr>).)*</tr>", 'Singleline').Value
    if (-not $row) { throw "No row for $deviceName on /admin/devices." }
    if ($row -match '>never<') { throw "$deviceName is listed but has never been seen; the agent did not heartbeat." }
    "$deviceName is listed with agent version $Version and a heartbeat."

    Write-Step 'The installed binaries report the release version'
    # The agent compares the installed version with the newest release to decide whether to update. A
    # binary that reports the wrong version makes every device either update forever or never update.
    foreach ($exe in 'AppPortal.exe', 'AppPortal.Agent.exe') {
        $info = (Get-Item (Join-Path $installDir $exe)).VersionInfo
        $product = ($info.ProductVersion -split '[+\- ]')[0]
        "$exe ProductVersion=$($info.ProductVersion) FileVersion=$($info.FileVersion)"
        if ($product -ne $Version) { throw "$exe reports $product, expected $Version." }
    }

    Write-Step "The installed files carry their signatures ($Signing)"
    # What the MSI put on disk, not what the build folder held: proof that WiX harvested the signed
    # payload rather than a copy from before signing.
    & $signatureScript -Mode $Signing -Path $installDir

    Write-Step 'The installed client starts, renders, and exits'
    # The Apps page, and the admin dashboard: section 10 signs the demo administrator in, so the admin
    # views are built, composed and drawn by the installed binaries rather than only in the build.
    foreach ($capture in @(@{ Name = 'installed-client.png'; Section = '0' }, @{ Name = 'installed-client-admin.png'; Section = '10' })) {
        $screenshot = Join-Path $LogDirectory $capture.Name
        $client = Start-Process -FilePath (Join-Path $installDir 'AppPortal.exe') -PassThru `
            -ArgumentList '--demo', '--screenshot', "`"$screenshot`"", $capture.Section
        if (-not $client.WaitForExit(90000)) {
            $client.Kill()
            throw "The installed AppPortal.exe did not exit within 90 seconds rendering section $($capture.Section)."
        }
        if ($client.ExitCode -ne 0) { throw "The installed AppPortal.exe exited with $($client.ExitCode) rendering section $($capture.Section)." }
        if (-not (Test-Path $screenshot) -or (Get-Item $screenshot).Length -lt 1024) {
            throw "The installed client wrote no screenshot of section $($capture.Section)."
        }
        "The installed client rendered section $($capture.Section) in $((Get-Item $screenshot).Length) bytes."
    }

    Write-Step 'A package from a package manager installs, shows as installed, and comes off again'
    # The device asks, as the client would. The agent installs through Windows PowerShell as SYSTEM,
    # reports the module list, and the server matches the row to the catalog app by its package id.
    $device = @{ Authorization = "Bearer $($settings.deviceToken)" }
    function Wait-ForInstall([string] $Id, [string] $What) {
        Wait-For -Description $What -Seconds 600 -Condition {
            $script:record = Invoke-RestMethod "$serverUrl/api/v1/installs/$Id" -Headers $device -TimeoutSec 10
            $script:record.state -in 'Succeeded', 'Failed', 'Cancelled'
        }
        if ($script:record.state -ne 'Succeeded') { throw "$What ended $($script:record.state): $($script:record.detail)" }
    }

    $install = Invoke-RestMethod "$serverUrl/api/v1/installs" -Method Post -Headers $device -TimeoutSec 10 `
        -ContentType 'application/json' -Body '{"appId":"ci-managed"}'
    Wait-ForInstall $install.id 'the managed install'
    if (-not (Test-Path $managedModulePath)) { throw "The install succeeded but $managedModulePath is not there." }
    Wait-For -Description 'the module to be listed under Installed as the catalog app' -Seconds 120 -Condition {
        @(Invoke-RestMethod "$serverUrl/api/v1/device/installed" -Headers $device -TimeoutSec 10 |
            ForEach-Object { $_ } | Where-Object { $_.catalogAppId -eq 'ci-managed' }).Count -gt 0
    }
    "$managedModule installed through Windows PowerShell and is listed under Installed."

    $removal = Invoke-RestMethod "$serverUrl/api/v1/uninstalls" -Method Post -Headers $device -TimeoutSec 10 `
        -ContentType 'application/json' -Body '{"appId":"ci-managed"}'
    Wait-ForInstall $removal.id 'the managed removal'
    if (Test-Path $managedModulePath) { throw "The removal succeeded but $managedModulePath is still there." }
    "$managedModule came off again."

    Write-Step 'Uninstall leaves nothing behind'
    Invoke-Msi -Operation '/x' -Name 'uninstall' -Package $msiPath | Out-Null
    if (Get-Service AppPortalAgent -ErrorAction SilentlyContinue) { throw 'The service survived uninstall.' }
    if (Test-Path $installDir) { throw 'The program files survived uninstall.' }
    'The service and the program files are gone.'

    # -----------------------------------------------------------------------------------------
    # Upgrade. An installed fleet meets this path, not the clean install above, so a package that
    # cannot upgrade the release before it is a package that cannot ship.
    # -----------------------------------------------------------------------------------------
    $previousPath = if ($PreviousMsi -and (Test-Path $PreviousMsi)) { (Resolve-Path $PreviousMsi).Path } else { $null }
    $previousVersion = $null
    if ($previousPath) {
        $previousVersion = (Read-MsiTable $previousPath "SELECT Value FROM Property WHERE Property='ProductVersion'") |
            Select-Object -First 1
    }

    # Windows Installer replaces an installed copy only when the incoming ProductVersion is higher. Once
    # the working version has shipped, the newest release carries that same version, the second install
    # lands beside the first instead of replacing it, and the count below reads two copies and blames the
    # installer for a version arithmetic problem. Upgrading a version over itself proves nothing, so this
    # says which two versions it compared and moves on. A real release is always higher than the one
    # before it, which is exactly when this check matters.
    if ($previousPath -and [version] $previousVersion -lt [version] $Version) {
        Write-Step 'Upgrade over the previous release'
        Remove-Item $settingsPath -ErrorAction SilentlyContinue
        Invoke-Msi -Operation '/i' -Name 'install-previous' -Package $previousPath `
            -Properties @("SERVERURL=$serverUrl", "ENROLLMENTKEY=$enrollmentKey") | Out-Null
        Wait-For -Description 'the previous release to enroll' -Seconds 120 -Condition {
            (Test-Path $settingsPath) -and
            -not [string]::IsNullOrWhiteSpace((Get-Content $settingsPath -Raw | ConvertFrom-Json).deviceToken)
        }
        $before = (Get-Content $settingsPath -Raw | ConvertFrom-Json).deviceToken

        Invoke-Msi -Operation '/i' -Name 'upgrade' -Package $msiPath | Out-Null
        $after = (Get-Content $settingsPath -Raw | ConvertFrom-Json).deviceToken
        if ($after -ne $before) { throw 'The upgrade replaced the device token; the PC would enroll twice.' }

        # Most keys under Uninstall have no DisplayName at all, and under Set-StrictMode comparing a
        # property that is not there throws rather than answering no. So ask whether it exists first.
        $entry = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' |
            Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'App Portal' })
        if ($entry.Count -ne 1) { throw "Programs and Features lists $($entry.Count) copies of App Portal, expected 1." }
        $entry = $entry[0]
        if ($entry.DisplayVersion -ne $Version) {
            throw "After the upgrade Programs and Features says $($entry.DisplayVersion), expected $Version."
        }
        (Get-Service AppPortalAgent).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
        'The upgrade kept the token, replaced the version, and left one installed copy.'

        Invoke-Msi -Operation '/x' -Name 'uninstall-upgraded' -Package $msiPath -Properties @('REMOVEDATA=1') | Out-Null
    }
    elseif ($previousPath) {
        Write-Step "Upgrade check skipped: the previous release is $previousVersion and so is this build"
        "Bump <Version> in Directory.Build.props to exercise the upgrade path."
    }
    else {
        Write-Step 'Upgrade check skipped: no previous MSI supplied'
    }
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    # Never leave a half-installed product, or its data directory, on a machine somebody runs this on
    # by hand or on a runner that keeps its disk. A failure here must not replace the failure that
    # brought us into this block, so report it and let the original stand.
    try { & $resetScript } catch { Write-Warning "Cleanup failed: $($_.Exception.Message)" }
    Remove-Item $managedModulePath -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Installer verification passed.' -ForegroundColor Green
