[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$taskName = "AiCliFeishuBridge"
$projectDirectory = Split-Path -Parent $PSScriptRoot
$controlExecutable = Join-Path $projectDirectory "AI CLI飞书助手.exe"
if (-not (Test-Path -LiteralPath $controlExecutable)) {
    throw "AI CLI Feishu Assistant executable was not found."
}
$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

$actionArguments = "--bridge-service"
$action = New-ScheduledTaskAction `
    -Execute $controlExecutable `
    -Argument $actionArguments `
    -WorkingDirectory $projectDirectory
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$principal = New-ScheduledTaskPrincipal `
    -UserId $currentUser `
    -LogonType Interactive `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew

$definition = New-ScheduledTask `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings

Register-ScheduledTask -TaskName $taskName -InputObject $definition -Force | Out-Null
Write-Output "Installed scheduled task: $taskName"
