<#
.SYNOPSIS
    What the two Windows installer scripts share: ci-installer-test.ps1, which the release gate runs, and
    Test-RealPc.ps1, which an operator runs by hand on a real PC.

.DESCRIPTION
    A module has a scope of its own and does not see the caller's variables, so everything a function
    needs is a parameter. A caller that always passes the same log directory can set it once through
    $PSDefaultParameterValues['Invoke-Msi:LogDirectory'].
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string] $Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Wait-For {
    param(
        [Parameter(Mandatory)] [scriptblock] $Condition,
        [Parameter(Mandatory)] [string] $Description,
        [int] $Seconds = 60
    )
    for ($i = 0; $i -lt $Seconds; $i++) {
        try { if (& $Condition) { return } } catch { }
        Start-Sleep -Seconds 1
    }
    throw "Timed out after $Seconds seconds waiting for: $Description"
}

function Invoke-Msi {
    param(
        [Parameter(Mandatory)] [string] $Operation,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Package,
        [Parameter(Mandatory)] [string] $LogDirectory,
        [string[]] $Properties = @()
    )
    $log = Join-Path $LogDirectory "$Name.log"
    $arguments = @($Operation, "`"$Package`"", '/qn', '/norestart', '/l*v', "`"$log`"") + $Properties
    $process = Start-Process msiexec.exe -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -notin 0, 3010) {
        throw "msiexec $Name exited $($process.ExitCode); see $log"
    }
    return $process.ExitCode
}

Export-ModuleMember -Function Write-Step, Wait-For, Invoke-Msi
