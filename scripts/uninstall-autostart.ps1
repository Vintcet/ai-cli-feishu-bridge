[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$taskName = "CodexFeishuBridge"
$stopScript = Join-Path $PSScriptRoot "stop-bridge.ps1"

& $stopScript
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
Write-Output "Removed scheduled task: $taskName"
