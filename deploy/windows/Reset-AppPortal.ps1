<#
.SYNOPSIS
    Takes every installed copy of App Portal off this machine and removes its data directory.

.DESCRIPTION
    Both installer checks assume they begin on a machine with no App Portal on it. That was free on a
    GitHub-hosted runner, which is thrown away after every job. It is not free on a runner that keeps its
    disk, and the two checks fail in different ways when it is not true.

    Verify-Msi.ps1 ends by asserting that REMOVEDATA=1 leaves nothing in %ProgramData%\AppPortal. Files
    an earlier run left there are not owned by the installer, so REMOVEDATA cannot take them, the
    directory survives, and the check reports that the installer leaked data when it did not.

    ci-installer-test.ps1 reads client.json to decide that the device enrolled. An earlier run's
    client.json still holds a device token, so that check can pass without this run enrolling at all.

    Uninstalls by ProductCode rather than by a package path: two copies can sit under one UpgradeCode
    with different ProductCodes, and /x against one package leaves the other in place.

    Safe to run when nothing is installed. Run it on a throwaway machine, never on a workstation that has
    App Portal on it for real: it removes the data directory, device token and all.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$dataDir = Join-Path $env:ProgramData 'AppPortal'

# Most keys under Uninstall carry no DisplayName at all, and under Set-StrictMode comparing a property
# that is not there throws rather than answering no. So ask whether it exists first.
$installed = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'App Portal' })

foreach ($entry in $installed) {
    $process = Start-Process msiexec.exe -ArgumentList @('/x', $entry.PSChildName, '/qn', '/norestart') -Wait -PassThru
    if ($process.ExitCode -notin 0, 3010) {
        throw "Uninstalling $($entry.PSChildName) exited $($process.ExitCode)."
    }
}

Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue

# Refuse to continue rather than let a later check read what is left and draw the wrong conclusion.
if (Test-Path $dataDir) { throw "$dataDir could not be removed; take it off by hand before running this again." }
if (Get-Service AppPortalAgent -ErrorAction SilentlyContinue) { throw 'AppPortalAgent survived removal.' }

"Removed $($installed.Count) installed copy or copies. No App Portal on this machine."
