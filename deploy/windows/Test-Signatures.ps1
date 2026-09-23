<#
.SYNOPSIS
    Checks that App Portal's own executables, libraries and MSI carry the signature a build promises.

.DESCRIPTION
    The release gate runs this after every signing stage, on everything the release carries, and on the
    files an installed MSI put in %ProgramFiles%\App Portal. A signing run in which SignPath skipped a
    file, or a copy step put back the unsigned one, fails here and names the file, rather than shipping
    and being found by the first agent that refuses the next update.

    The files it judges are the project's own: names that start with AppPortal and end in .exe, .dll or
    .msi, found directly in each directory given or passed as files. Every other PE file in a directory
    is listed with its signer, or "unsigned", and never fails the check: Avalonia, SkiaSharp and the .NET
    runtime ship as their authors signed them.

    Every failure is collected before the script stops, so one run names every file that needs fixing.

.PARAMETER Path
    Files or directories. Directories are not searched recursively.

.PARAMETER Mode
    Off     Prints one line per file and fails nothing. An unsigned build.
    Test    Each own file carries a signature whose file hash matches. The certificate chains to a root
            the machine does not trust, so Valid is not expected.
    Release Each own file is Valid, signed by -Publisher, and timestamped, so the signature outlives the
            certificate.

.PARAMETER Publisher
    The simple name of the signer certificate a release must carry.

.EXAMPLE
    ./deploy/windows/Test-Signatures.ps1 -Mode Release -Path out/client, out/agent, out/setup/AppPortalSetup.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Path,
    [Parameter(Mandatory)] [ValidateSet('Off', 'Test', 'Release')] [string] $Mode,
    [string] $Publisher = 'SignPath Foundation'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-OwnFile([System.IO.FileInfo] $File) {
    $File.Name -like 'AppPortal*' -and $File.Extension -in '.exe', '.dll', '.msi'
}

function Get-SignerName($Signature) {
    if ($null -eq $Signature.SignerCertificate) { return $null }
    $Signature.SignerCertificate.GetNameInfo('SimpleName', $false)
}

# Own files are judged; every other PE file in a directory given is only reported.
$own = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
$others = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
foreach ($entry in $Path) {
    if (-not (Test-Path $entry)) { throw "$entry does not exist." }
    $item = Get-Item $entry
    if ($item -is [System.IO.DirectoryInfo]) {
        foreach ($file in Get-ChildItem $item.FullName -File) {
            if (Test-OwnFile $file) { $own.Add($file) }
            elseif ($file.Extension -in '.exe', '.dll') { $others.Add($file) }
        }
    }
    elseif (Test-OwnFile $item) { $own.Add($item) }
    else { throw "$entry is not an App Portal file (AppPortal*.exe, .dll or .msi)." }
}

if ($own.Count -eq 0) { throw "No App Portal files in $($Path -join ', ')." }

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($file in $own) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    $signer = Get-SignerName $signature
    $timestamped = $null -ne $signature.TimeStamperCertificate
    "{0}: {1}, signer {2}, {3}" -f $file.Name, $signature.Status, ($signer ?? 'none'), ($timestamped ? 'timestamped' : 'no timestamp')

    # For an unsigned or altered file StatusMessage talks about running scripts, which says nothing
    # useful about a binary.
    $problem = switch ($Mode) {
        'Release' {
            if ($signature.Status -eq 'NotSigned') { 'not signed' }
            elseif ($signature.Status -eq 'HashMismatch') { 'the file does not match its signature' }
            elseif ($signature.Status -ne 'Valid') { "status $($signature.Status): $($signature.StatusMessage)" }
            elseif ($signer -ne $Publisher) { "signed by $signer, expected $Publisher" }
            elseif (-not $timestamped) { 'no timestamp; the signature would expire with the certificate' }
        }
        'Test' {
            if ($signature.Status -eq 'NotSigned' -or $null -eq $signature.SignerCertificate) { 'not signed' }
            elseif ($signature.Status -eq 'HashMismatch') { 'the file does not match its signature' }
        }
    }
    if ($problem) { $failures.Add("$($file.FullName): $problem") }
}

foreach ($file in $others) {
    $signer = Get-SignerName (Get-AuthenticodeSignature -LiteralPath $file.FullName)
    "  (not judged) {0}: {1}" -f $file.Name, ($signer ?? 'unsigned')
}

if ($failures.Count -gt 0) {
    throw "$($failures.Count) of $($own.Count) App Portal files fail the $Mode signature check:`n  $($failures -join "`n  ")"
}

"$($own.Count) App Portal files pass the $Mode signature check."
