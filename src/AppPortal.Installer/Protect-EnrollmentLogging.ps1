[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Path)
$ErrorActionPreference = 'Stop'

# WiX 5's FormatFile actions omit HideTarget. Masking the property alone still exposes its contents
# in the deferred execution log, so set the MSI flag before this package can leave the build.
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase((Resolve-Path $Path).Path, 1)
try {
    foreach ($action in 'Wix4ExecFormatFiles_X64', 'Wix4RollbackFormatFiles_X64') {
        $view = $database.OpenView("SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action`` = '$action'")
        $view.Execute()
        $record = $view.Fetch()
        if (-not $record) { throw "Expected WiX action $action is missing." }
        $type = $record.IntegerData(1) -bor 0x2000
        $view.Close()
        $view = $database.OpenView("UPDATE ``CustomAction`` SET ``Type`` = $type WHERE ``Action`` = '$action'")
        $view.Execute()
        $view.Close()
    }
    $database.Commit()
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
