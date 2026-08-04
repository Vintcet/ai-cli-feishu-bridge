[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$taskName = "AiCliFeishuBridge"
$projectDirectory = Split-Path -Parent $PSScriptRoot
$controlExecutable = Join-Path $projectDirectory "AI CLI飞书助手.exe"

if (Test-Path -LiteralPath $controlExecutable) {
    $stopProcess = Start-Process `
        -FilePath $controlExecutable `
        -ArgumentList "--bridge-stop" `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($stopProcess.ExitCode -ne 0) {
        Write-Warning "The bridge could not be stopped cleanly (exit code $($stopProcess.ExitCode))."
    }
}
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
Write-Output "Removed scheduled task: $taskName"
