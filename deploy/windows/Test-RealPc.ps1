#Requires -Version 7.2
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Proves on a real Windows 11 PC that a per-user install and an install that finishes at a restart both
    work, and writes a PASS or FAIL summary for the release pull request.

.DESCRIPTION
    The release gate's Windows job proves machine-wide installs only. A GitHub-hosted runner has nobody
    signed in, so every per-user install parks there, and a job cannot survive a restart. This script is
    the part a person runs by hand, before every release, on a disposable PC or VM.

    It acts as the device. The agent writes the device token to %ProgramData%\AppPortal\client.json
    when it enrolls, and the client asks for an install with exactly one request: POST /api/v1/installs
    with that token and X-AppPortal-User naming the signed-in account. The script sends the same request
    with the test account in the header, and restarts the PC with the command the client's Restart
    button runs. It resumes itself after the restart from a scheduled task.

    Everything it installs is a purpose-built installer compiled on the PC from ProofInstaller.cs, served
    over loopback HTTP by the script itself. The server runs on the PC too, from the build output, in
    fake Action1 mode, so the boot time and the server's clock are one clock and no administrator
    credential crosses a network.

    It installs App Portal, deletes %ProgramData%\AppPortal, and restarts the PC. Never run it on a PC
    that matters.

    OPERATOR RUNBOOK

    1. Prepare a disposable Windows 11 PC or VM with PowerShell 7; the .NET 10 SDK, or the ASP.NET Core
       10 runtime plus -ServerDll; a checkout of the release commit; and the AppPortal-msi artifact from
       the full gate run on that commit.
    2. Create a standard local account, sign in to it, and sign every other account out. Windows refuses
       the client's restart command while another account is signed in (shutdown.exe exit code 1191),
       so the script refuses to start then. In the account's Settings > Accounts > Sign-in options, turn
       off "Use my sign-in info to automatically finish setting up after an update": shutdown /g signs
       the account straight back in after the restart, and the parked-install check needs nobody signed
       in. Give it a distinctive name, for example apptester: the summary replaces every occurrence of
       the account name with <account>, so a short common word would blank out parts of the details.
    3. In the test account's session, open PowerShell 7 as administrator through UAC with administrator
       credentials.
    4. Run:
           ./deploy/windows/Test-RealPc.ps1 -Msi <path> -Account <user or COMPUTER\user> -Disposable
               [-ServerDll <path>] [-Port 5090]
       It asks for a password for the proof's server administrator unless APPPORTAL_ADMIN_PASSWORD is
       set. That password also signs in at http://127.0.0.1:<port>/admin while the proof runs.
    5. The PC restarts after 60 seconds. Wait at the sign-in screen for two minutes, then sign in as the
       test account. Signing in sooner makes the parked-install check NOT RUN.
    6. Run ./deploy/windows/Test-RealPc.ps1 -Result elevated. It waits for the resume phase, prints the
       summary, and exits 0 on PASS, 1 on FAIL and 2 while the proof is still running. Attach
       %ProgramData%\AppPortalProof\real-pc-proof.md to the release pull request.

    After a PASS the proof removes everything it installed, App Portal included. After a FAIL it leaves
    App Portal installed for inspection; run ./deploy/windows/Test-RealPc.ps1 -Cleanup when done.

    WHAT IT CHECKS, IN ORDER

    per-user-session      The per-user installer ran as the account, unelevated, in its session.
    per-user-profile      Its files and uninstall entry are in the account's profile and hive, and
                          nothing is in the SYSTEM profile or HKLM.
    per-user-reported     The install names the account, and the account's Installed list shows it,
                          but not without the header or with another account's header.
    per-user-transcript   The install finished while the app its installer started was still running,
                          and the job log holds the installer's output.
    per-user-removal      Removal as the account takes the files and entry away and the list forgets it.
    restart-pending       Two installs that need a restart wait at "Restart to finish", and the admin
                          list filtered to restarts shows both.
    restart-confirmed     After the restart both succeed on their own, with one row each, installed as
                          SYSTEM, and the machine-wide Installed list shows them.
    parked-until-sign-in  A per-user install asked for while nobody was signed in waited for the
                          account and ran into its profile after the sign-in.

    CREDENTIALS

    The administrator password is never written to a file, a log, a transcript or a command line. It is
    in the process environment only for the 'admin add' call, and is used once more to take an admin API
    token for the restart-pending check; that token is revoked before the restart. The device token is
    read from client.json into a variable and never printed. No Windows password is asked for or stored,
    and the resume phase needs no credential.

    STATE

    Everything lives in %ProgramData%\AppPortalProof, restricted to SYSTEM and Administrators: state.json
    (no secrets), server-data, packages, logs, the summary, and copies of the scripts and the server
    that the SYSTEM task runs, so nothing SYSTEM runs can be changed by a standard account. The resume
    task is AppPortalProofResume.

.PARAMETER Msi
    The AppPortal-<version>-x64.msi the full gate built from the commit under test.

.PARAMETER Account
    The standard local account that is signed in, as user or COMPUTER\user.

.PARAMETER Disposable
    Says that this PC may be changed and restarted. The script refuses to start without it.

.PARAMETER ServerDll
    A built AppPortal.Server.dll. Built from the checkout with the .NET SDK when not supplied.

.PARAMETER Port
    The loopback port for the server. The package host takes the next one.

.PARAMETER Resume
    The phase after the restart. The scheduled task runs it as SYSTEM; it is not for use by hand.

.PARAMETER Result
    Waits for the resume phase to finish and prints the summary.

.PARAMETER Cleanup
    Removes everything the proof left, App Portal included, apart from the summary and the logs.
#>
[CmdletBinding(DefaultParameterSetName = 'Start')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Start')] [string] $Msi,
    [Parameter(Mandatory, ParameterSetName = 'Start')] [string] $Account,
    [Parameter(ParameterSetName = 'Start')]
    [Parameter(ParameterSetName = 'Cleanup')] [switch] $Disposable,
    [Parameter(ParameterSetName = 'Start')] [string] $ServerDll,
    [Parameter(ParameterSetName = 'Start')] [ValidateRange(1024, 65000)] [int] $Port = 5090,
    [Parameter(Mandatory, ParameterSetName = 'Resume')] [switch] $Resume,
    [Parameter(Mandatory, ParameterSetName = 'Result')] [switch] $Result,
    [Parameter(Mandatory, ParameterSetName = 'Cleanup')] [switch] $Cleanup
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'InstallerTestHelpers.psm1') -Force

$StateRoot = Join-Path $env:ProgramData 'AppPortalProof'
$StatePath = Join-Path $StateRoot 'state.json'
$SummaryPath = Join-Path $StateRoot 'real-pc-proof.md'
$LogRoot = Join-Path $StateRoot 'logs'
$ServerData = Join-Path $StateRoot 'server-data'
$PackageRoot = Join-Path $StateRoot 'packages'
$ScriptCopy = Join-Path $StateRoot 'scripts'
$ServerCopy = Join-Path $StateRoot 'server'
$TaskName = 'AppPortalProofResume'
$ServiceName = 'AppPortalAgent'
$AdminName = 'proof-admin'
$AppPortalData = Join-Path $env:ProgramData 'AppPortal'
$ClientJson = Join-Path $AppPortalData 'client.json'
$JobLogs = Join-Path $AppPortalData 'jobs'
$ProofFolder = 'App Portal Proof'
$UninstallBranch = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
$SystemSid = 'S-1-5-18'
$Terminal = @('Succeeded', 'Failed', 'Cancelled')

# =================================================================================================
# Pure helpers. No system calls, so they can be tried against sample data without a PC to spare.
# =================================================================================================

function Get-CheckNames {
    @('per-user-session', 'per-user-profile', 'per-user-reported', 'per-user-transcript', 'per-user-removal',
        'restart-pending', 'restart-confirmed', 'parked-until-sign-in')
}

function Hide-Identity {
    <#
    .SYNOPSIS
        Takes the computer and the account out of a line of text. The summary is attached to a public
        pull request, so it must never name either, nor carry a URL, a token or a SID.
    #>
    param(
        [AllowEmptyString()] [AllowNull()] [string] $Text,
        [string[]] $Computer = @(),
        [string] $Account
    )
    if ([string]::IsNullOrEmpty($Text)) { return '' }

    $out = [regex]::Replace($Text, 'https?://[^\s|)]+', '<url>')
    $out = [regex]::Replace($out, '\b(apd|apa|ape)_[A-Za-z0-9_\-]+', '<token>')
    $out = [regex]::Replace($out, 'S-1-5-21(-\d+)+', '<sid>')

    # Longest first, so a computer name that contains the user name, or the other way round, is taken
    # out whole rather than leaving half of it behind.
    $names = [System.Collections.Generic.List[object]]::new()
    if ($Account) {
        $names.Add(@{ Name = $Account; Mask = '<account>' })
        $names.Add(@{ Name = ($Account -split '\\')[-1]; Mask = '<account>' })
    }
    foreach ($name in $Computer) {
        if ($name) { $names.Add(@{ Name = $name; Mask = '<pc>' }) }
    }
    foreach ($entry in $names | Sort-Object { $_.Name.Length } -Descending) {
        $out = [regex]::Replace($out, [regex]::Escape($entry.Name), $entry.Mask,
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    return $out
}

function Format-Utc([object] $UnixSeconds) {
    if ($null -eq $UnixSeconds -or "$UnixSeconds" -eq '') { return 'unknown' }
    [DateTimeOffset]::FromUnixTimeSeconds([long] $UnixSeconds).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
}

function Format-ProofSummary {
    <#
    .SYNOPSIS
        The summary, as text, from the proof's state. Every check without a result reads NOT RUN, and the
        heading reads PASS only when all eight passed.
    #>
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $State,
        [string[]] $Computer = @(),
        [string] $Account
    )
    $checks = if ($State.Contains('checks') -and $State['checks']) { $State['checks'] } else { @{} }
    $rows = foreach ($name in Get-CheckNames) {
        $check = if ($checks.Contains($name)) { $checks[$name] } else { $null }
        $outcome = if ($check -and $check['result'] -in 'PASS', 'FAIL', 'NOT RUN') { $check['result'] } else { 'NOT RUN' }
        $detail = if ($check -and $check['detail']) { [string] $check['detail'] } else { 'The proof did not reach this check.' }
        $detail = (Hide-Identity -Text $detail -Computer $Computer -Account $Account) -replace '[\r\n]+', ' ' -replace '\|', '\|'
        [pscustomobject]@{ Name = $name; Result = $outcome; Detail = $detail.Trim() }
    }
    $verdict = if (@($rows | Where-Object Result -ne 'PASS').Count -eq 0) { 'PASS' } else { 'FAIL' }

    $build = if ($State['agentBuild']) { [string] $State['agentBuild'] } else { 'unknown' }
    $buildNote = switch ($State['buildSource']) {
        'ProductVersion' { ' (ProductVersion of the installed AppPortal.Agent.exe)' }
        'checkout' { ' (ProductVersion of the installed AppPortal.Agent.exe carries no commit; the commit is git rev-parse HEAD of the checkout)' }
        default { '' }
    }
    $windows = Hide-Identity -Text ([string] $State['windows']) -Computer $Computer -Account $Account

    $lines = @(
        "# App Portal real-PC proof: $verdict"
        ''
        "- Agent build: $build$buildNote"
        "- MSI SHA-256: $(if ($State['msiSha256']) { $State['msiSha256'] } else { 'unknown' })"
        "- Windows: $(if ($windows) { $windows } else { 'unknown' })"
        '- Test account: standard user'
        "- Started / Finished: $(Format-Utc $State['startedAt']) / $(Format-Utc $State['finishedAt'])"
        ''
        '| Check | Result | Detail |'
        '|---|---|---|'
    )
    $lines += foreach ($row in $rows) { "| $($row.Name) | $($row.Result) | $($row.Detail) |" }
    return (($lines -join "`n") + "`n")
}

function New-ProofCatalog {
    <#
    .SYNOPSIS
        The three proof apps as a catalog file. Each is the one proof installer with different arguments,
        so the agent downloads and verifies it once.
    #>
    param(
        [Parameter(Mandatory)] [string] $Url,
        [Parameter(Mandatory)] [string] $Sha256,
        [Parameter(Mandatory)] [long] $SizeBytes
    )
    $apps = foreach ($app in @(
            @{ Id = 'proof-user'; Scope = 'user'; Arguments = 'install --id proof-user --scope user --linger 300'; Reboot = $false },
            @{ Id = 'proof-restart-code'; Scope = 'machine'; Arguments = 'install --id proof-restart-code --scope machine --exit 3010'; Reboot = $false },
            @{ Id = 'proof-restart-flag'; Scope = 'machine'; Arguments = 'install --id proof-restart-flag --scope machine'; Reboot = $true })) {
        [ordered]@{
            id = $app.Id
            name = "App Portal Proof $($app.Id)"
            publisher = 'App Portal'
            description = 'Installed by the real-PC proof. Safe to remove.'
            category = 'Other'
            userRemovable = $true
            match = [ordered]@{ nameEquals = "App Portal Proof $($app.Id)" }
            agent = [ordered]@{
                kind = 'direct'
                url = $Url
                sha256 = $Sha256.ToLowerInvariant()
                installerType = 'exe'
                silentArgs = $app.Arguments
                sizeBytes = $SizeBytes
                uninstallKey = "AppPortalProof-$($app.Id)"
                scope = $app.Scope
                requiresReboot = $app.Reboot
            }
        }
    }
    return ([ordered]@{ apps = @($apps) } | ConvertTo-Json -Depth 6)
}

# =================================================================================================
# State and the summary on disk.
# =================================================================================================

function Get-UnixNow { [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }

function Read-State {
    if (-not (Test-Path $StatePath)) { return $null }
    return (Get-Content $StatePath -Raw | ConvertFrom-Json -AsHashtable)
}

function Save-State {
    $script:State | ConvertTo-Json -Depth 8 | Set-Content -Path $StatePath -Encoding utf8NoBOM
}

function Set-Check {
    param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [ValidateSet('PASS', 'FAIL', 'NOT RUN')] [string] $Outcome,
        [Parameter(Mandatory)] [string] $Detail)
    if (-not $script:State.Contains('checks') -or $null -eq $script:State.checks) { $script:State.checks = [ordered]@{} }
    $script:State.checks[$Name] = [ordered]@{ result = $Outcome; detail = $Detail }
    Save-State
    $colour = switch ($Outcome) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host "[$Outcome] $Name - $Detail" -ForegroundColor $colour
}

function Test-CheckDone([string] $Name) {
    $script:State.Contains('checks') -and $script:State.checks -and $script:State.checks.Contains($Name)
}

function Complete-Checks([string] $Reason) {
    foreach ($name in Get-CheckNames) {
        if (-not (Test-CheckDone $name)) { Set-Check $name 'NOT RUN' $Reason }
    }
}

function Write-Summary {
    $script:State.finishedAt = Get-UnixNow
    $text = Format-ProofSummary -State $script:State -Computer (Get-ComputerNames) -Account $script:State.account
    [System.IO.File]::WriteAllText($SummaryPath, $text, [System.Text.UTF8Encoding]::new($false))
    $script:State.result = if ($text.StartsWith('# App Portal real-PC proof: PASS')) { 'PASS' } else { 'FAIL' }
    $script:State.phase = 'finished'
    Save-State
    return $script:State.result
}

function Get-ComputerNames {
    # The NetBIOS name is cut to 15 characters; the host name is not, and either could appear.
    @($env:COMPUTERNAME, [System.Net.Dns]::GetHostName()) | Where-Object { $_ } | Select-Object -Unique
}

# =================================================================================================
# The PC: accounts, sessions, profiles, processes.
# =================================================================================================

function Resolve-ProofAccount([string] $Name) {
    if ($Name -notmatch '\\') { $Name = "$env:COMPUTERNAME\$Name" }
    try {
        $sid = ([System.Security.Principal.NTAccount] $Name).Translate([System.Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "Refusing: there is no account '$Name' on this PC."
    }
    # The name as Windows spells it, which is how the agent will report the session.
    $canonical = $sid.Translate([System.Security.Principal.NTAccount]).Value
    return [pscustomobject]@{ Name = $canonical; Sid = $sid.Value }
}

function Test-LocalAdministrator([string] $Sid) {
    # By SID, because the group's name is translated on a non-English Windows.
    $group = ([System.Security.Principal.SecurityIdentifier] 'S-1-5-32-544').Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
    $members = ([ADSI] "WinNT://$env:COMPUTERNAME/$group,group").psbase.Invoke('Members')
    foreach ($member in @($members)) {
        $bytes = $member.GetType().InvokeMember('objectSid', 'GetProperty', $null, $member, $null)
        if ($bytes -and ([System.Security.Principal.SecurityIdentifier]::new([byte[]] $bytes, 0)).Value -eq $Sid) { return $true }
    }
    return $false
}

function Get-AccountSession([string] $Sid) {
    <# The session the account's desktop runs in, or null when it is not signed in. #>
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'")) {
        $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid -ErrorAction SilentlyContinue
        if ($owner -and $owner.ReturnValue -eq 0 -and $owner.Sid -eq $Sid) { return [int] $process.SessionId }
    }
    return $null
}

function Get-OtherSignedIn([string] $Sid) {
    <# Every other account with a desktop on this PC, as DOMAIN\user, the operator's own included. #>
    $names = foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'")) {
        $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid -ErrorAction SilentlyContinue
        if ($owner -and $owner.ReturnValue -eq 0 -and $owner.Sid -ne $Sid) {
            try { ([System.Security.Principal.SecurityIdentifier] $owner.Sid).Translate([System.Security.Principal.NTAccount]).Value }
            catch { $owner.Sid }
        }
    }
    @($names | Sort-Object -Unique)
}

function Test-AutomaticSignIn([string] $Sid) {
    <#
    Whether Winlogon signs this account in on its own after a restart. It is on unless the account opted
    out in Sign-in options, which Windows keeps under UserARSO by SID, or a policy turns it off for all.
    #>
    $policy = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue
    if ($policy -and $policy.PSObject.Properties['DisableAutomaticRestartSignOn'] -and $policy.DisableAutomaticRestartSignOn -eq 1) { return $false }
    $choice = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\UserARSO\$Sid" -ErrorAction SilentlyContinue
    return -not ($choice -and $choice.PSObject.Properties['OptOut'] -and $choice.OptOut -eq 1)
}

function Get-ProfilePath([string] $Sid) {
    $userProfile = Get-CimInstance Win32_UserProfile -Filter "SID='$Sid'"
    if (-not $userProfile) { throw "No profile for the account; sign in to it once before the proof." }
    return $userProfile.LocalPath
}

function Get-LingeringChild([string] $Directory) {
    @(Get-CimInstance Win32_Process -Filter "Name='proof-installer.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($Directory, [StringComparison]::OrdinalIgnoreCase) })
}

function Stop-ProofProcesses {
    Get-CimInstance Win32_Process -Filter "Name='proof-installer.exe'" | ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Read-JsonFile([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable)
}

function Test-UninstallEntry([string] $Root, [string] $Id) {
    Test-Path -LiteralPath "Registry::$Root\$UninstallBranch\AppPortalProof-$Id"
}

# =================================================================================================
# The server, the package host and the API.
# =================================================================================================

function Start-PackageHost([string] $Directory, [int] $HostPort) {
    <#
    .SYNOPSIS
        Serves the files in one directory over loopback HTTP, in a runspace of this process, so it lives
        exactly as long as the phase that started it. It ignores Range, which the agent's resumable
        download accepts: a 200 with the whole body replaces the partial file.
    #>
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://127.0.0.1:$HostPort/")
    $listener.Start()
    $shell = [powershell]::Create()
    $null = $shell.AddScript({
            param($Listener, $Directory)
            while ($Listener.IsListening) {
                try { $context = $Listener.GetContext() } catch { break }
                try {
                    $name = [System.IO.Path]::GetFileName($context.Request.Url.AbsolutePath)
                    $path = if ($name) { Join-Path $Directory $name } else { $null }
                    if ($path -and [System.IO.File]::Exists($path)) {
                        $bytes = [System.IO.File]::ReadAllBytes($path)
                        $context.Response.ContentType = 'application/octet-stream'
                        $context.Response.ContentLength64 = $bytes.Length
                        if ($context.Request.HttpMethod -ne 'HEAD') { $context.Response.OutputStream.Write($bytes, 0, $bytes.Length) }
                    }
                    else {
                        $context.Response.StatusCode = 404
                    }
                }
                catch { }
                finally { try { $context.Response.Close() } catch { } }
            }
        }).AddArgument($listener).AddArgument($Directory)
    $null = $shell.BeginInvoke()
    return [pscustomobject]@{ Listener = $listener; Shell = $shell }
}

function Stop-PackageHost($PackageHost) {
    if (-not $PackageHost) { return }
    try { $PackageHost.Listener.Stop(); $PackageHost.Listener.Close() } catch { }
    try { $PackageHost.Shell.Stop(); $PackageHost.Shell.Dispose() } catch { }
}

function Set-ServerEnvironment {
    $env:Action1__Mode = 'Fake'
    $env:Portal__DataDirectory = $ServerData
    $env:ASPNETCORE_URLS = $script:State.serverUrl
    # The agent and the server must read what this proof set up, not a developer override left in the
    # environment, and nothing the server starts may inherit the administrator password.
    Remove-Item Env:APPPORTAL_CONFIG, Env:APPPORTAL_SERVER_URL, Env:APPPORTAL_DEVICE_TOKEN, Env:APPPORTAL_ADMIN_PASSWORD `
        -ErrorAction SilentlyContinue
}

function Invoke-ServerCli {
    param([Parameter(Mandatory)] [string[]] $Arguments)
    # The server's content root is its working directory, and appsettings.json is read from there.
    Push-Location $ServerCopy
    try {
        $output = & $script:State.dotnet (Join-Path $ServerCopy 'AppPortal.Server.dll') @Arguments
        if ($LASTEXITCODE -ne 0) { throw "AppPortal.Server $($Arguments[0]) $($Arguments[1]) failed." }
        return $output
    }
    finally { Pop-Location }
}

function Start-ProofServer([string] $Phase) {
    Set-ServerEnvironment
    $server = Start-Process $script:State.dotnet -ArgumentList "`"$(Join-Path $ServerCopy 'AppPortal.Server.dll')`"" -PassThru `
        -WorkingDirectory $ServerCopy `
        -RedirectStandardOutput (Join-Path $LogRoot "server-$Phase.log") `
        -RedirectStandardError (Join-Path $LogRoot "server-$Phase-error.log")
    $script:State.serverPid = $server.Id
    Save-State
    Wait-For -Description 'the server to answer /healthz' -Seconds 90 -Condition {
        $null -ne (Invoke-RestMethod "$($script:State.serverUrl)/healthz" -TimeoutSec 2)
    }
    return $server
}

function Stop-ProofServer {
    $serverPid = if ($script:State -and $script:State.Contains('serverPid')) { $script:State.serverPid } else { $null }
    if ($serverPid) {
        $process = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
        if ($process -and $process.ProcessName -eq 'dotnet') { Stop-Process -Id $serverPid -Force -ErrorAction SilentlyContinue }
    }
    # And any server a crashed phase left, recognised by running from the proof's own copy.
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($ServerCopy, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Invoke-Api {
    <#
    .SYNOPSIS
        One call to the proof server. The answer comes back as hashtables, so a field the server leaves
        out reads as null instead of stopping the script under strict mode. Errors name the route and
        the server's message, never a header.
    #>
    param(
        [string] $Method = 'GET',
        [Parameter(Mandatory)] [string] $Path,
        [hashtable] $Headers = @{},
        [object] $Body
    )
    $arguments = @{
        Uri = "$($script:State.serverUrl)$Path"; Method = $Method; Headers = $Headers
        TimeoutSec = 30; SkipHttpErrorCheck = $true
    }
    if ($null -ne $Body) {
        $arguments.Body = ConvertTo-Json -InputObject $Body -Compress -Depth 5
        $arguments.ContentType = 'application/json'
    }
    $response = Invoke-WebRequest @arguments
    $text = [string] $response.Content
    if ($response.StatusCode -ge 400) {
        $message = try { (ConvertFrom-Json $text -AsHashtable).message } catch { $null }
        throw "$Method $($Path.Split('?')[0]) answered $($response.StatusCode)$(if ($message) { ": $message" })"
    }
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return (ConvertFrom-Json $text -AsHashtable)
}

function Invoke-Device {
    param([string] $Method = 'GET', [Parameter(Mandatory)] [string] $Path, [string] $User, [object] $Body)
    $headers = @{ Authorization = "Bearer $script:DeviceToken" }
    if ($User) { $headers['X-AppPortal-User'] = $User }
    Invoke-Api -Method $Method -Path $Path -Headers $headers -Body $Body
}

function Read-DeviceToken {
    $settings = Read-JsonFile $ClientJson
    if (-not $settings -or -not $settings['deviceToken']) { throw 'client.json carries no device token.' }
    $script:DeviceToken = [string] $settings['deviceToken']
}

function Request-Install([string] $AppId, [string] $User, [switch] $Removal) {
    $route = if ($Removal) { '/api/v1/uninstalls' } else { '/api/v1/installs' }
    Invoke-Device -Method Post -Path $route -User $User -Body @{ appId = $AppId }
}

function Wait-Install {
    param([Parameter(Mandatory)] [string] $Id, [int] $Seconds = 600, [scriptblock] $Until)
    if (-not $Until) { $Until = { param($record) $record['state'] -in $Terminal } }
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $record = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $record = Invoke-Device -Path "/api/v1/installs/$Id"
            if (& $Until $record) { return $record }
        }
        catch { Write-Host "  (still waiting: $($_.Exception.Message))" }
        Start-Sleep -Seconds 1
    }
    return $record
}

function Get-InstalledIds([string] $User) {
    @(Invoke-Device -Path '/api/v1/device/installed' -User $User | ForEach-Object { $_ } |
        Where-Object { $_ -and $_['catalogAppId'] } | ForEach-Object { $_['catalogAppId'] })
}

function Wait-Listed {
    param([string] $AppId, [string] $User, [bool] $Present = $true, [int] $Seconds = 420)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { if (((Get-InstalledIds $User) -contains $AppId) -eq $Present) { return $true } } catch { }
        Start-Sleep -Seconds 5
    }
    return $false
}

function Describe([object] $Record) {
    if (-not $Record) { return 'no answer from the server' }
    "$($Record['state']), rebootState $(if ($Record['rebootState']) { $Record['rebootState'] } else { 'none' }), detail '$($Record['detail'])'"
}

# =================================================================================================
# Cleanup, shared by the start of a proof, a passing resume phase and -Cleanup.
# =================================================================================================

function Remove-ProofRegistryEntries {
    param([string] $AccountSid, [string] $ProfilePath)
    foreach ($view in 'Registry64', 'Registry32') {
        $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', $view)
        try {
            $branch = $machine.OpenSubKey($UninstallBranch, $true)
            if ($branch) {
                $branch.GetSubKeyNames() | Where-Object { $_ -like 'AppPortalProof-*' } |
                    ForEach-Object { $branch.DeleteSubKeyTree($_, $false) }
                $branch.Dispose()
            }
        }
        finally { $machine.Dispose() }
    }

    # Every hive that is loaded, which is every account signed in and SYSTEM's.
    foreach ($hive in Get-ChildItem Registry::HKEY_USERS -ErrorAction SilentlyContinue) {
        Get-ChildItem "Registry::$($hive.Name)\$UninstallBranch" -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -like 'AppPortalProof-*' } |
            ForEach-Object { Remove-Item -LiteralPath $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue }
    }

    # The test account's hive when it is signed out, loaded for long enough to take the entry out.
    if ($AccountSid -and $ProfilePath -and -not (Test-Path "Registry::HKEY_USERS\$AccountSid")) {
        $hivePath = Join-Path $ProfilePath 'NTUSER.DAT'
        if (Test-Path -LiteralPath $hivePath) {
            & reg.exe load 'HKU\AppPortalProofCleanup' $hivePath | Out-Null
            if ($LASTEXITCODE -eq 0) {
                try {
                    Get-ChildItem "Registry::HKEY_USERS\AppPortalProofCleanup\$UninstallBranch" -ErrorAction SilentlyContinue |
                        Where-Object { $_.PSChildName -like 'AppPortalProof-*' } |
                        ForEach-Object { Remove-Item -LiteralPath $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue }
                }
                finally {
                    [GC]::Collect()
                    [GC]::WaitForPendingFinalizers()
                    & reg.exe unload 'HKU\AppPortalProofCleanup' | Out-Null
                }
            }
        }
    }
}

function Invoke-ProofCleanup {
    param([string] $ResetScript, [switch] $KeepSummary)
    try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop } catch { }
    Stop-ProofServer
    Stop-ProofProcesses

    # A reset that fails must not stop the rest of the cleanup; it is reported, and the rest still goes.
    try { & $ResetScript }
    catch { Write-Warning "Removing App Portal failed: $($_.Exception.Message) Run Reset-AppPortal.ps1 again by hand." }

    Remove-Item (Join-Path $env:ProgramFiles $ProofFolder) -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($userProfile in @(Get-CimInstance Win32_UserProfile)) {
        if ($userProfile.LocalPath) {
            Remove-Item (Join-Path $userProfile.LocalPath "AppData\Local\$ProofFolder") -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $state = if ($script:State) { $script:State } else { Read-State }
    $accountSid = if ($state -and $state.Contains('accountSid')) { $state.accountSid } else { $null }
    $profilePath = if ($state -and $state.Contains('profilePath')) { $state.profilePath } else { $null }
    Remove-ProofRegistryEntries -AccountSid $accountSid -ProfilePath $profilePath

    if (-not (Test-Path $StateRoot)) { return }
    if (-not ($KeepSummary -and (Test-Path $SummaryPath))) {
        Remove-Item $StateRoot -Recurse -Force -ErrorAction SilentlyContinue
        return
    }
    Get-ChildItem $StateRoot -Force | Where-Object { $_.Name -notin 'real-pc-proof.md', 'logs' } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    # What -Result reads, cut down to the verdict: the paths, port and ids have nothing left to name.
    $verdict = if ((Get-Content $SummaryPath -TotalCount 1) -eq '# App Portal real-PC proof: PASS') { 'PASS' } else { 'FAIL' }
    [ordered]@{ phase = 'finished'; result = $verdict; cleanedAt = (Get-UnixNow) } |
        ConvertTo-Json | Set-Content -Path $StatePath -Encoding utf8NoBOM
}

function Protect-StateDirectory {
    New-Item -ItemType Directory -Force $StateRoot | Out-Null
    # SYSTEM and Administrators only. The SYSTEM task runs what is in here, so nobody else may write it.
    & icacls.exe $StateRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict $StateRoot to SYSTEM and Administrators." }
    foreach ($directory in $LogRoot, $ServerData, $PackageRoot, $ScriptCopy) {
        New-Item -ItemType Directory -Force $directory | Out-Null
    }
}

# =================================================================================================
# Start phase.
# =================================================================================================

function Assert-Preflight {
    if (-not $Disposable) {
        throw 'Refusing: -Disposable is missing. This script installs App Portal, deletes %ProgramData%\AppPortal and restarts the PC. Run it only on a PC or VM you can throw away, and say so with -Disposable.'
    }
    if ($env:GITHUB_ACTIONS) {
        throw 'Refusing: this is GitHub Actions. A runner has nobody signed in and cannot survive a restart; run the proof by hand on a disposable PC.'
    }
    $existing = Read-State
    if ($existing -and $existing.phase -in 'handed-over', 'resuming') {
        throw "Refusing: a proof is already in progress (phase $($existing.phase)). Run -Result to wait for it, or -Cleanup to abandon it."
    }

    $resolved = Resolve-ProofAccount $Account
    if ($resolved.Sid -eq ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value) {
        throw 'Refusing: the test account is the account running this script. Use a separate standard account.'
    }
    if (Test-LocalAdministrator $resolved.Sid) {
        throw "Refusing: $($resolved.Name) is a local administrator. The proof needs a standard account, or it cannot tell an unelevated install from an elevated one."
    }
    $session = Get-AccountSession $resolved.Sid
    if ($null -eq $session) {
        throw "Refusing: $($resolved.Name) is not signed in. Sign in to it and leave it signed in."
    }
    $others = @(Get-OtherSignedIn $resolved.Sid)
    if ($others.Count -gt 0) {
        # Found now rather than at the hand-over, which comes after ten minutes of checks.
        throw "Refusing: $($others -join ', ') is also signed in. Windows refuses the client's restart command while another account is signed in (shutdown.exe exit code 1191). Sign the other accounts out and run this from $($resolved.Name)'s session, as administrator through UAC."
    }
    if (Test-AutomaticSignIn $resolved.Sid) {
        throw "Refusing: Windows will sign $($resolved.Name) back in by itself after the restart, because shutdown /g restarts into the last session while 'Use my sign-in info to automatically finish setting up after an update' is on. Nobody would ever be signed out, so the parked-install check could not run. Turn it off in $($resolved.Name)'s Settings > Accounts > Sign-in options."
    }

    $build = [int] (Get-CimInstance Win32_OperatingSystem).BuildNumber
    if ($build -lt 22000) { Write-Warning "This is Windows build $build. The proof is meant for Windows 11 (build 22000 or later)." }

    if (-not (Test-Path -LiteralPath $Msi)) { throw "Refusing: there is no MSI at $Msi." }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Refusing: dotnet is not on the PATH. Install the .NET 10 SDK, or the ASP.NET Core 10 runtime and pass -ServerDll.'
    }
    foreach ($candidate in $Port, ($Port + 1)) {
        $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $candidate)
        try { $probe.Start() } catch { throw "Refusing: port $candidate is in use. Pick another with -Port." } finally { $probe.Stop() }
    }
    return [pscustomobject]@{ Account = $resolved; Session = $session }
}

function Read-AdminPassword {
    # Taken out of the environment at once, so nothing this script starts inherits it by accident.
    $fromEnvironment = $env:APPPORTAL_ADMIN_PASSWORD
    Remove-Item Env:APPPORTAL_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    if ($fromEnvironment) {
        $secure = ConvertTo-SecureString $fromEnvironment -AsPlainText -Force
        $fromEnvironment = $null
    }
    else {
        $secure = Read-Host -AsSecureString "Password for the proof's server administrator (12 characters or more)"
    }
    if ($secure.Length -lt 12) { throw 'Refusing: the administrator password must be at least 12 characters.' }
    return $secure
}

function ConvertFrom-SecurePassword([securestring] $Secure) {
    [System.Net.NetworkCredential]::new('', $Secure).Password
}

function Build-ProofInstaller {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { throw "The in-box C# compiler is not at $csc." }
    $source = (Resolve-Path (Join-Path $PSScriptRoot 'ProofInstaller.cs')).Path
    $output = Join-Path $PackageRoot 'proof-installer.exe'
    # Full paths only: csc reads a forward slash as the start of an option.
    & $csc /nologo /target:exe /platform:anycpu /warnaserror "/out:$output" $source
    if ($LASTEXITCODE -ne 0) { throw 'ProofInstaller.cs did not compile.' }
    return Get-Item $output
}

function Get-AgentBuild([string] $RepositoryRoot) {
    $agent = Join-Path $env:ProgramFiles 'App Portal\AppPortal.Agent.exe'
    $version = (Get-Item $agent).VersionInfo.ProductVersion
    if ($version -match '\+[0-9a-f]{7,}') {
        return @{ Build = $version; Source = 'ProductVersion' }
    }
    $commit = 'unknown'
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $head = & git -C $RepositoryRoot rev-parse HEAD
        if ($LASTEXITCODE -eq 0 -and $head) { $commit = "$head".Trim() }
    }
    return @{ Build = "$version+$commit"; Source = 'checkout' }
}

function Invoke-PerUserChecks {
    $account = $script:State.account
    $sid = $script:State.accountSid
    $session = Get-AccountSession $sid
    $userRoot = Join-Path $script:State.profilePath "AppData\Local\$ProofFolder\proof-user"
    $systemLocal = Join-Path (Get-ProfilePath $SystemSid) 'AppData\Local'

    Write-Step "Per-user install of proof-user as the account"
    $requested = Get-UnixNow
    try {
        $install = Request-Install 'proof-user' $account
        $record = Wait-Install -Id $install['id'] -Seconds 600
    }
    catch {
        Write-Host "  The request failed: $($_.Exception.Message)"
        $record = $null
    }
    # Straight after the state turns, before anything else: the point is that the child is still alive.
    $lingering = @(Get-LingeringChild $userRoot)
    $took = (Get-UnixNow) - $requested
    Write-Host "  $(Describe $record) after $took s; $($lingering.Count) lingering child process(es)."

    if (-not $record -or $record['state'] -ne 'Succeeded') {
        $why = "The per-user install ended $(Describe $record)."
        foreach ($name in 'per-user-session', 'per-user-profile', 'per-user-reported', 'per-user-transcript') { Set-Check $name 'FAIL' $why }
        Set-Check 'per-user-removal' 'NOT RUN' 'The per-user install did not succeed, so there was nothing to remove.'
        Stop-ProofProcesses
        return
    }

    try {
        $installed = Read-JsonFile (Join-Path $userRoot 'installed.json')
        if (-not $installed) {
            Set-Check 'per-user-session' 'FAIL' "No installed.json in the account's profile."
        }
        else {
            $problems = @()
            if ($installed['sid'] -ne $sid) { $problems += "ran as $($installed['account']), not the account" }
            if ($installed['elevated']) { $problems += 'ran elevated' }
            if ($null -eq $session -or [int] $installed['sessionId'] -ne $session) {
                $problems += "ran in session $($installed['sessionId']), the account's session is $session"
            }
            if ($problems) { Set-Check 'per-user-session' 'FAIL' ($problems -join '; ') }
            else { Set-Check 'per-user-session' 'PASS' "Ran as $account, not elevated, in the account's session $session." }
        }
    }
    catch { Set-Check 'per-user-session' 'FAIL' $_.Exception.Message }

    try {
        $problems = @()
        $installed = Read-JsonFile (Join-Path $userRoot 'installed.json')
        $expectedLocal = Join-Path $script:State.profilePath 'AppData\Local'
        if (-not (Test-Path (Join-Path $userRoot 'proof-installer.exe'))) { $problems += "the program is not under the account's %LOCALAPPDATA%" }
        if ($installed -and -not [string]::Equals($installed['localAppData'], $expectedLocal, [StringComparison]::OrdinalIgnoreCase)) {
            $problems += "the installer saw %LOCALAPPDATA% as $($installed['localAppData'])"
        }
        if (-not (Test-UninstallEntry "HKEY_USERS\$sid" 'proof-user')) { $problems += "no uninstall entry in the account's hive" }
        if (Test-Path (Join-Path $systemLocal "$ProofFolder\proof-user")) { $problems += 'files in the SYSTEM profile' }
        if (Test-Path (Join-Path $env:ProgramFiles "$ProofFolder\proof-user")) { $problems += 'files under Program Files' }
        if ((Test-UninstallEntry 'HKEY_LOCAL_MACHINE' 'proof-user') -or
            (Test-Path -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\AppPortalProof-proof-user') -or
            (Test-UninstallEntry "HKEY_USERS\$SystemSid" 'proof-user')) {
            $problems += 'an uninstall entry in HKLM or the SYSTEM hive'
        }
        if ($problems) { Set-Check 'per-user-profile' 'FAIL' ($problems -join '; ') }
        else { Set-Check 'per-user-profile' 'PASS' "Files under the account's own profile and the entry under HKU\<account SID>; nothing in the SYSTEM profile, Program Files or HKLM." }
    }
    catch { Set-Check 'per-user-profile' 'FAIL' $_.Exception.Message }

    try {
        $problems = @()
        if (-not [string]::Equals($record['requestedBy'], $account, [StringComparison]::OrdinalIgnoreCase)) {
            $problems += "requestedBy is '$($record['requestedBy'])'"
        }
        if (-not (Wait-Listed -AppId 'proof-user' -User $account -Present $true)) {
            $problems += "the account's Installed list did not show proof-user within 7 minutes"
        }
        if ((Get-InstalledIds $null) -contains 'proof-user') { $problems += 'the list without X-AppPortal-User shows it' }
        $stranger = "$env:COMPUTERNAME\appportal-proof-nobody"
        if ((Get-InstalledIds $stranger) -contains 'proof-user') { $problems += "another account's list shows it" }
        if ($problems) { Set-Check 'per-user-reported' 'FAIL' ($problems -join '; ') }
        else { Set-Check 'per-user-reported' 'PASS' "requestedBy is the account; its Installed list shows proof-user; the list without the header and another account's list do not." }
    }
    catch { Set-Check 'per-user-reported' 'FAIL' $_.Exception.Message }

    try {
        $problems = @()
        if ($lingering.Count -eq 0) {
            $problems += "the install reached Succeeded after $took s, when the child its installer started had already exited"
        }
        $line = @(Select-String -Path (Join-Path $JobLogs '*.log') -SimpleMatch 'App Portal Proof proof-user installed for user scope' -ErrorAction SilentlyContinue)
        if ($line.Count -eq 0) { $problems += "no job log under %ProgramData%\AppPortal\jobs holds the installer's line" }
        if ($problems) { Set-Check 'per-user-transcript' 'FAIL' ($problems -join '; ') }
        else { Set-Check 'per-user-transcript' 'PASS' "Succeeded after $took s while the child its installer started was still running; the job log holds the installer's line." }
    }
    catch { Set-Check 'per-user-transcript' 'FAIL' $_.Exception.Message }
    finally { Stop-ProofProcesses }

    Write-Step 'Per-user removal of proof-user as the account'
    try {
        $removal = Request-Install 'proof-user' $account -Removal
        $removed = Wait-Install -Id $removal['id'] -Seconds 600
        $problems = @()
        if (-not $removed -or $removed['state'] -ne 'Succeeded') { $problems += "the removal ended $(Describe $removed)" }
        if (Test-Path (Join-Path $userRoot 'installed.json')) { $problems += 'installed.json is still there' }
        if (Test-UninstallEntry "HKEY_USERS\$sid" 'proof-user') { $problems += "the entry is still in the account's hive" }
        if (-not (Wait-Listed -AppId 'proof-user' -User $account -Present $false)) {
            $problems += "the account's Installed list still showed proof-user after 7 minutes"
        }
        if ($problems) { Set-Check 'per-user-removal' 'FAIL' ($problems -join '; ') }
        else { Set-Check 'per-user-removal' 'PASS' "Removed as the account: installed.json and the entry are gone, and its Installed list no longer shows proof-user." }
    }
    catch { Set-Check 'per-user-removal' 'FAIL' $_.Exception.Message }
}

function Invoke-RestartPendingCheck([securestring] $Password) {
    $account = $script:State.account
    Write-Step 'Two installs that finish at a restart'
    $ids = [ordered]@{}
    $problems = @()
    foreach ($app in 'proof-restart-code', 'proof-restart-flag') {
        try {
            $install = Request-Install $app $account
            $ids[$app] = $install['id']
            $record = Wait-Install -Id $install['id'] -Seconds 600 -Until {
                param($r) ($r['state'] -eq 'Running' -and $r['rebootState'] -eq 'pending') -or $r['state'] -in $Terminal
            }
            if (-not $record -or $record['state'] -ne 'Running' -or $record['rebootState'] -ne 'pending' -or $record['detail'] -ne 'Restart to finish') {
                $problems += "$app is $(Describe $record)"
            }
        }
        catch { $problems += "$app could not be requested: $($_.Exception.Message)" }
    }
    $script:State.installs = $ids
    Save-State

    # The admin view of the same installs. The token lives for this block only and is revoked in it.
    $adminHeaders = $null
    try {
        $signIn = Invoke-Api -Method Post -Path '/api/v1/admin/session' -Body @{
            username = $AdminName; password = (ConvertFrom-SecurePassword $Password); deviceName = 'real-pc-proof'
        }
        $adminHeaders = @{ Authorization = "Bearer $($signIn['token'])" }
        $signIn = $null
        $page = Invoke-Api -Path '/api/v1/admin/installs?restart=1&limit=200' -Headers $adminHeaders
        $listed = @($page['items'] | ForEach-Object { $_['id'] })
        foreach ($app in $ids.Keys) {
            if ($listed -notcontains $ids[$app]) { $problems += "the admin installs list filtered to restarts does not list $app" }
        }
    }
    catch { $problems += "the admin check failed: $($_.Exception.Message)" }
    finally {
        if ($adminHeaders) {
            try { Invoke-Api -Method Delete -Path '/api/v1/admin/session' -Headers $adminHeaders | Out-Null }
            catch { Write-Warning "Could not revoke the admin token: $($_.Exception.Message)" }
        }
        $adminHeaders = $null
    }

    if ($problems) { Set-Check 'restart-pending' 'FAIL' ($problems -join '; ') }
    else { Set-Check 'restart-pending' 'PASS' "Both installs are Running with rebootState pending and detail 'Restart to finish', and the admin list filtered with restart=1 lists both." }
}

function Invoke-StartPhase {
    $preflight = Assert-Preflight
    $password = Read-AdminPassword
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
    $msiPath = (Resolve-Path $Msi).Path

    Write-Step 'Leave nothing of an earlier proof or App Portal in place'
    $script:State = $null
    Invoke-ProofCleanup -ResetScript (Join-Path $PSScriptRoot 'Reset-AppPortal.ps1')
    Protect-StateDirectory
    Start-Transcript -Path (Join-Path $LogRoot 'start.log') | Out-Null

    $os = Get-CimInstance Win32_OperatingSystem
    $ubr = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue).UBR
    $script:State = [ordered]@{
        phase = 'starting'
        result = $null
        account = $preflight.Account.Name
        accountSid = $preflight.Account.Sid
        profilePath = Get-ProfilePath $preflight.Account.Sid
        port = $Port
        serverUrl = "http://127.0.0.1:$Port"
        packagePort = $Port + 1
        pwsh = (Get-Process -Id $PID).Path
        dotnet = (Get-Command dotnet).Source
        msiSha256 = (Get-FileHash $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
        windows = "$("$($os.Caption)".Trim()) (build $($os.BuildNumber)$(if ($ubr) { ".$ubr" }))"
        agentBuild = $null
        buildSource = $null
        startedAt = Get-UnixNow
        finishedAt = $null
        installs = [ordered]@{}
        checks = [ordered]@{}
    }
    Save-State

    $packageHost = $null
    $handedOver = $false
    try {
        Write-Step 'Copy what the SYSTEM task will run into the protected state directory'
        foreach ($file in 'Test-RealPc.ps1', 'InstallerTestHelpers.psm1', 'Reset-AppPortal.ps1', 'ProofInstaller.cs') {
            Copy-Item (Join-Path $PSScriptRoot $file) $ScriptCopy -Force
        }
        if (-not $ServerDll) {
            Push-Location $repositoryRoot
            try {
                dotnet build src/AppPortal.Server -c Release
                if ($LASTEXITCODE -ne 0) { throw 'The server build failed.' }
            }
            finally { Pop-Location }
            $ServerDll = Join-Path $repositoryRoot 'src/AppPortal.Server/bin/Release/net10.0/AppPortal.Server.dll'
        }
        $serverDirectory = Split-Path (Resolve-Path $ServerDll).Path
        Copy-Item $serverDirectory $ServerCopy -Recurse -Force
        if (-not (Test-Path (Join-Path $ServerCopy 'AppPortal.Server.dll'))) { throw 'The server copy has no AppPortal.Server.dll.' }

        Write-Step 'Build the proof installer and serve it on loopback'
        $installer = Build-ProofInstaller
        $sha256 = (Get-FileHash $installer.FullName -Algorithm SHA256).Hash
        $packageHost = Start-PackageHost -Directory $PackageRoot -HostPort $script:State.packagePort
        $packageUrl = "http://127.0.0.1:$($script:State.packagePort)/proof-installer.exe"
        "proof-installer.exe, $($installer.Length) bytes, SHA-256 $($sha256.ToLowerInvariant())."

        Write-Step 'Set up the server: administrator, proof catalog, enrollment key'
        Set-ServerEnvironment
        $env:APPPORTAL_ADMIN_PASSWORD = ConvertFrom-SecurePassword $password
        try { Invoke-ServerCli -Arguments 'admin', 'add', '--username', $AdminName | Out-Host }
        finally { Remove-Item Env:APPPORTAL_ADMIN_PASSWORD -ErrorAction SilentlyContinue }

        $catalogFile = Join-Path $StateRoot 'proof-catalog.json'
        New-ProofCatalog -Url $packageUrl -Sha256 $sha256 -SizeBytes $installer.Length | Set-Content -Path $catalogFile -Encoding utf8NoBOM
        Invoke-ServerCli -Arguments 'catalog', 'import', $catalogFile | Out-Host

        $keyOutput = Invoke-ServerCli -Arguments 'key', 'create', '--name', 'real-pc-proof', '--engine', 'agent', '--max-uses', '1'
        $enrollmentKey = $keyOutput | ForEach-Object { "$_" } | Where-Object { $_ -match '^ape_' } | Select-Object -Last 1
        if (-not $enrollmentKey) { throw 'No enrollment key was printed.' }
        $keyOutput = $null

        Write-Step 'Start the server'
        Start-ProofServer -Phase 'start' | Out-Null
        "Server is up."

        Write-Step 'Install the MSI and wait for enrollment'
        Invoke-Msi -Operation '/i' -Name 'install' -Package $msiPath -LogDirectory $LogRoot `
            -Properties @("SERVERURL=$($script:State.serverUrl)", "ENROLLMENTKEY=$enrollmentKey") | Out-Null
        $enrollmentKey = $null
        (Get-Service $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
        Wait-For -Description 'client.json to carry a device token' -Seconds 120 -Condition {
            $settings = Read-JsonFile $ClientJson
            $settings -and $settings['deviceToken']
        }
        Read-DeviceToken
        $agentBuild = Get-AgentBuild $repositoryRoot
        $script:State.agentBuild = $agentBuild.Build
        $script:State.buildSource = $agentBuild.Source
        Save-State
        "The device enrolled. Agent build $($agentBuild.Build)."

        Invoke-PerUserChecks
        Invoke-RestartPendingCheck $password

        Write-Step 'Hand over to the resume phase and restart'
        $script:State.phase = 'handed-over'
        $script:State.handedOverAt = Get-UnixNow
        Save-State

        $taskScript = Join-Path $ScriptCopy 'Test-RealPc.ps1'
        $action = New-ScheduledTaskAction -Execute $script:State.pwsh `
            -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$taskScript`" -Resume"
        $trigger = New-ScheduledTaskTrigger -AtStartup
        $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 2) -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries -StartWhenAvailable
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

        # A local server comes up after the agent at boot, and an enrolled agent whose first heartbeat
        # fails waits 900 seconds to try again. Manual across the restart, so its first start after the
        # boot meets a live server, which is what a fleet PC meets.
        Set-Service $ServiceName -StartupType Manual

        Stop-PackageHost $packageHost
        $packageHost = $null
        Stop-ProofServer

        # The command the client's Restart button runs.
        & shutdown.exe /g /t 60 /c "App Portal is finishing the installation of App Portal Proof."
        if ($LASTEXITCODE -ne 0) { throw "shutdown.exe exited $LASTEXITCODE." }
        $handedOver = $true

        Write-Host ''
        Write-Host 'The PC restarts in 60 seconds.' -ForegroundColor Green
        Write-Host "Wait at the sign-in screen for two minutes, then sign in as $($script:State.account)."
        Write-Host 'Then run, elevated: ./deploy/windows/Test-RealPc.ps1 -Result'
    }
    catch {
        Write-Host "The start phase stopped: $($_.Exception.Message)" -ForegroundColor Red
        Complete-Checks "The start phase stopped: $($_.Exception.Message)"
    }
    finally {
        Stop-PackageHost $packageHost
        if (-not $handedOver) {
            try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop } catch { }
            if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
                Set-Service $ServiceName -StartupType Automatic -ErrorAction SilentlyContinue
            }
            Stop-ProofServer
            $verdict = Write-Summary
            Write-Host ''
            Write-Host "Proof $verdict. Summary: $SummaryPath" -ForegroundColor Red
            Write-Host 'App Portal is left installed for inspection. Run ./deploy/windows/Test-RealPc.ps1 -Cleanup when done.'
        }
        $script:DeviceToken = $null
        try { Stop-Transcript | Out-Null } catch { }
    }
    if (-not $handedOver) { exit 1 }
}

# =================================================================================================
# Resume phase, as SYSTEM, at startup.
# =================================================================================================

function Invoke-ResumePhase {
    # First, so that whatever happens next cannot run again at every boot.
    try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop } catch { }

    $script:State = Read-State
    if (-not $script:State -or $script:State.phase -ne 'handed-over') { return }
    Start-Transcript -Path (Join-Path $LogRoot 'resume.log') | Out-Null
    $script:State.phase = 'resuming'
    $script:State.resumePid = $PID
    $script:State.resumedAt = Get-UnixNow
    Save-State

    $account = $script:State.account
    $sid = $script:State.accountSid
    $packageHost = $null
    try {
        Write-Step 'Start the package host and the server on the same data directory'
        $packageHost = Start-PackageHost -Directory $PackageRoot -HostPort $script:State.packagePort
        Start-ProofServer -Phase 'resume' | Out-Null
        Read-DeviceToken

        Write-Step 'Ask for the per-user install while the agent is still stopped'
        $signedInEarly = $null -ne (Get-AccountSession $sid)
        $parked = Request-Install 'proof-user' $account
        $script:State.installs['proof-user-parked'] = $parked['id']
        $script:State.signedInEarly = $signedInEarly
        Save-State
        "Account signed in already: $signedInEarly."

        Write-Step 'Start the agent and restore automatic start'
        Start-Service $ServiceName
        Set-Service $ServiceName -StartupType Automatic

        Write-Step 'Watch the restart installs and the parked install'
        $waiting = "Waiting for $account to sign in."
        $deadline = $script:State.resumedAt + 30 * 60
        $restart = @{}
        $parkedRecord = $null
        $sawWaitingAt = $null
        $parkedDoneAt = $null
        while ((Get-UnixNow) -lt $deadline) {
            foreach ($app in 'proof-restart-code', 'proof-restart-flag') {
                if ($restart.Contains($app) -or -not $script:State.installs.Contains($app)) { continue }
                try {
                    $record = Invoke-Device -Path "/api/v1/installs/$($script:State.installs[$app])"
                    if ($record['state'] -in $Terminal) { $restart[$app] = $record; "  $app is $(Describe $record)." }
                }
                catch { }
            }
            if (-not $parkedDoneAt) {
                try {
                    $parkedRecord = Invoke-Device -Path "/api/v1/installs/$($parked['id'])"
                    if (-not $sawWaitingAt -and $parkedRecord['detail'] -eq $waiting) { $sawWaitingAt = Get-UnixNow; "  proof-user is waiting for the account." }
                    if ($parkedRecord['state'] -in $Terminal) { $parkedDoneAt = Get-UnixNow; "  proof-user is $(Describe $parkedRecord)." }
                }
                catch { }
            }
            $restartDone = $restart.Count -eq @($script:State.installs.Keys | Where-Object { $_ -like 'proof-restart-*' }).Count
            if ($restartDone -and ($parkedDoneAt -or $signedInEarly)) { break }
            Start-Sleep -Seconds 2
        }

        try {
            $problems = @()
            $history = @(Invoke-Device -Path '/api/v1/installs?refresh=false' | ForEach-Object { $_ })
            foreach ($app in 'proof-restart-code', 'proof-restart-flag') {
                $record = if ($restart.Contains($app)) { $restart[$app] } else { $null }
                if (-not $record) { $problems += "$app did not finish within 30 minutes of the boot"; continue }
                if ($record['state'] -ne 'Succeeded' -or $record['rebootState'] -ne 'confirmed' -or $record['detail'] -ne 'Installed. The PC has restarted.') {
                    $problems += "$app is $(Describe $record)"
                }
                $rows = @($history | Where-Object { $_['appId'] -eq $app -and $_['kind'] -eq 'install' }).Count
                if ($rows -ne 1) { $problems += "$app has $rows install rows" }
                $installed = Read-JsonFile (Join-Path $env:ProgramFiles "$ProofFolder\$app\installed.json")
                if (-not $installed) { $problems += "$app has no installed.json under Program Files" }
                elseif ($installed['sid'] -ne $SystemSid) { $problems += "$app ran as $($installed['account']), not SYSTEM" }
                if (-not (Wait-Listed -AppId $app -User $null -Present $true -Seconds 600)) {
                    $problems += "the machine-wide Installed list does not show $app"
                }
            }
            if ($problems) { Set-Check 'restart-confirmed' 'FAIL' ($problems -join '; ') }
            else { Set-Check 'restart-confirmed' 'PASS' "Both succeeded after the restart with rebootState confirmed, one install row each, installed as SYSTEM, and listed machine-wide." }
        }
        catch { Set-Check 'restart-confirmed' 'FAIL' $_.Exception.Message }

        try {
            if ($signedInEarly) {
                Set-Check 'parked-until-sign-in' 'NOT RUN' 'signed in too early; wait at the sign-in screen'
            }
            elseif (-not $parkedDoneAt) {
                Set-Check 'parked-until-sign-in' 'FAIL' "The parked install did not finish within 30 minutes of the boot: $(Describe $parkedRecord)."
            }
            elseif (-not $sawWaitingAt -and $parkedRecord['state'] -eq 'Succeeded') {
                # It ran without ever waiting, so the account was signed in by the time the agent got to it.
                Set-Check 'parked-until-sign-in' 'NOT RUN' 'signed in too early; wait at the sign-in screen'
            }
            else {
                $problems = @()
                if (-not $sawWaitingAt) { $problems += "it never showed '$waiting'" }
                if ($parkedRecord['state'] -ne 'Succeeded') { $problems += "it ended $(Describe $parkedRecord)" }
                $installed = Read-JsonFile (Join-Path $script:State.profilePath "AppData\Local\$ProofFolder\proof-user\installed.json")
                if (-not $installed -or $installed['sid'] -ne $sid) { $problems += "installed.json is not in the account's profile or names another account" }
                if ($problems) { Set-Check 'parked-until-sign-in' 'FAIL' ($problems -join '; ') }
                else {
                    $minutes = [math]::Round(($parkedDoneAt - $script:State.resumedAt) / 60, 1)
                    Set-Check 'parked-until-sign-in' 'PASS' "Showed 'Waiting for <account> to sign in.', then succeeded into the account's profile $minutes minutes after the boot."
                }
            }
        }
        catch { Set-Check 'parked-until-sign-in' 'FAIL' $_.Exception.Message }
    }
    catch {
        Write-Host "The resume phase stopped: $($_.Exception.Message)" -ForegroundColor Red
        Complete-Checks "The resume phase stopped: $($_.Exception.Message)"
    }
    finally {
        Complete-Checks 'The resume phase did not reach this check.'
        try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop } catch { }
        if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
            Set-Service $ServiceName -StartupType Automatic -ErrorAction SilentlyContinue
            Start-Service $ServiceName -ErrorAction SilentlyContinue
        }
        Stop-ProofProcesses
        Stop-PackageHost $packageHost
        Stop-ProofServer
        $script:DeviceToken = $null
        $verdict = Write-Summary
        "Proof $verdict. Summary: $SummaryPath"
        if ($verdict -eq 'PASS') {
            try { Stop-Transcript | Out-Null } catch { }
            Invoke-ProofCleanup -ResetScript (Join-Path $PSScriptRoot 'Reset-AppPortal.ps1') -KeepSummary
        }
        else {
            'App Portal is left installed for inspection. Run ./deploy/windows/Test-RealPc.ps1 -Cleanup when done.'
            try { Stop-Transcript | Out-Null } catch { }
        }
    }
}

# =================================================================================================
# -Result and -Cleanup.
# =================================================================================================

function Invoke-ResultPhase {
    $deadline = [DateTime]::UtcNow.AddMinutes(60)
    $announced = $null
    while ($true) {
        $state = Read-State
        if (-not $state) {
            Write-Host "No proof has run on this PC: there is no $StatePath."
            exit 1
        }
        if ($state.phase -eq 'finished' -and -not (Test-Path $SummaryPath)) {
            Write-Host 'The proof finished without a summary.'
            exit 1
        }
        if ($state.phase -eq 'finished') {
            Get-Content $SummaryPath -Raw | Write-Host
            Write-Host "Summary: $SummaryPath"
            if ($state['result'] -eq 'PASS') { exit 0 }
            if (Test-Path $LogRoot) { Write-Host "Logs: $LogRoot" }
            exit 1
        }
        if ($state.phase -eq 'resuming' -and $state.Contains('resumePid') -and $state.resumePid -and
            -not (Get-Process -Id $state.resumePid -ErrorAction SilentlyContinue)) {
            Write-Host "The resume phase stopped without writing a summary. Its transcript is $(Join-Path $LogRoot 'resume.log')."
            exit 1
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            Write-Host "The proof is still running (phase $($state.phase)). Run -Result again later."
            exit 2
        }
        if ($announced -ne $state.phase) {
            $announced = $state.phase
            $what = switch ($state.phase) {
                'handed-over' { 'Waiting for the resume phase to start. It runs at startup; has the PC restarted?' }
                'resuming' { 'The resume phase is running. It can take up to 30 minutes after the boot.' }
                default { "Waiting (phase $($state.phase))." }
            }
            Write-Host $what
        }
        Start-Sleep -Seconds 10
    }
}

function Invoke-CleanupPhase {
    if (-not (Test-Path $StateRoot) -and -not $Disposable) {
        throw 'Refusing: no proof state on this PC. -Cleanup also removes App Portal; pass -Disposable to do that anyway.'
    }
    $script:State = Read-State
    if ($script:State -and $script:State.Contains('resumePid') -and $script:State.resumePid) {
        $resume = Get-Process -Id $script:State.resumePid -ErrorAction SilentlyContinue
        if ($resume -and $resume.ProcessName -eq 'pwsh') { Stop-Process -Id $resume.Id -Force }
    }
    if ($script:State -and (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
        Set-Service $ServiceName -StartupType Automatic -ErrorAction SilentlyContinue
    }
    Invoke-ProofCleanup -ResetScript (Join-Path $PSScriptRoot 'Reset-AppPortal.ps1') -KeepSummary
    Write-Host 'Removed the proof task, processes, files, uninstall entries and App Portal.'
    if (Test-Path $SummaryPath) { Write-Host "The summary and logs stay in $StateRoot." }
}

# =================================================================================================

$script:State = $null
$script:DeviceToken = $null
switch ($PSCmdlet.ParameterSetName) {
    'Start' { Invoke-StartPhase }
    'Resume' { Invoke-ResumePhase }
    'Result' { Invoke-ResultPhase }
    'Cleanup' { Invoke-CleanupPhase }
}
