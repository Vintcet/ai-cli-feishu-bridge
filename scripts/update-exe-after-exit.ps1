[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$TargetProcessId,

    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = "Stop"
$projectDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sourcePath = [IO.Path]::GetFullPath($Source)
$destinationPath = [IO.Path]::GetFullPath($Destination)
$prefix = $projectDirectory.TrimEnd('\') + '\'
if (-not $sourcePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $destinationPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Updater paths must remain inside the bridge directory."
}
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Staged executable does not exist."
}

while (Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue) {
    Start-Sleep -Milliseconds 500
}
for ($attempt = 0; $attempt -lt 30; $attempt += 1) {
    try {
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
        exit 0
    } catch {
        Start-Sleep -Milliseconds 500
    }
}
exit 1
