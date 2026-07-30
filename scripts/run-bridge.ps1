[CmdletBinding()]
param(
    [ValidateRange(0, 65535)]
    [int]$Port = 0
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent $PSScriptRoot
$entryFile = Join-Path $projectDirectory "dist\index.js"

Set-Location -LiteralPath $projectDirectory

if ($Port -gt 0) {
    $env:BRIDGE_HTTP_PORT = $Port.ToString([Globalization.CultureInfo]::InvariantCulture)
}

if (-not (Test-Path -LiteralPath $entryFile)) {
    $npmExecutable = (Get-Command npm.cmd -ErrorAction Stop).Source
    & $npmExecutable run build
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$nodeExecutable = (Get-Command node.exe -ErrorAction Stop).Source
& $nodeExecutable $entryFile
exit $LASTEXITCODE
