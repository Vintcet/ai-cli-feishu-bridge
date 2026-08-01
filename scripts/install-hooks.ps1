[CmdletBinding()]
param(
    [ValidateRange(0, 65535)]
    [int]$Port = 0
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent $PSScriptRoot
$normalizedProject = $projectDirectory.Replace("\", "/")
$hooksDirectory = Join-Path $env:USERPROFILE ".codex"
$hooksFile = Join-Path $hooksDirectory "hooks.json"

if (Test-Path -LiteralPath $hooksFile) {
    $raw = Get-Content -LiteralPath $hooksFile -Raw
    $config = if ([string]::IsNullOrWhiteSpace($raw)) {
        [ordered]@{}
    } else {
        $raw | ConvertFrom-Json -AsHashtable
    }
} else {
    $config = [ordered]@{}
}
if ($null -eq $config) {
    $config = [ordered]@{}
}
if (-not $config.Contains("hooks") -or $null -eq $config["hooks"]) {
    $config["hooks"] = [ordered]@{}
}
$hooks = $config["hooks"]
$before = $config | ConvertTo-Json -Depth 30 -Compress

function New-BridgeHookCommand {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptName,
        [Parameter(Mandatory = $true)][int]$Timeout,
        [Parameter(Mandatory = $true)][string]$StatusMessage
    )
    $command = "node `"$normalizedProject/dist/hooks/$ScriptName.js`""
    return [ordered]@{
        type = "command"
        command = $command
        commandWindows = $command
        timeout = $Timeout
        statusMessage = $StatusMessage
    }
}

function New-BridgeHookGroup {
    param(
        [string]$Matcher,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Hook
    )
    $group = [ordered]@{}
    if (-not [string]::IsNullOrWhiteSpace($Matcher)) {
        $group["matcher"] = $Matcher
    }
    $group["hooks"] = @($Hook)
    return $group
}

function Test-IsBridgeHookGroup {
    param([object]$Group)
    if ($null -eq $Group -or $Group -isnot [System.Collections.IDictionary]) {
        return $false
    }
    $bridgeHookPattern = '/dist/hooks/(?:session-start|session-end|permission|request-user-input|activity|stop)\.js'
    $directCommand = ([string]$Group["command"]).Replace('\', '/')
    $directCommandWindows = ([string]$Group["commandWindows"]).Replace('\', '/')
    if ($directCommand -match $bridgeHookPattern -or
        $directCommandWindows -match $bridgeHookPattern) {
        return $true
    }
    foreach ($hook in @($Group["hooks"])) {
        if ($null -eq $hook -or $hook -isnot [System.Collections.IDictionary]) {
            continue
        }
        $command = ([string]$hook["command"]).Replace('\', '/')
        $commandWindows = ([string]$hook["commandWindows"]).Replace('\', '/')
        if ($command -match $bridgeHookPattern -or
            $commandWindows -match $bridgeHookPattern) {
            return $true
        }
    }
    return $false
}

function Set-BridgeHookGroups {
    param(
        [Parameter(Mandatory = $true)][string]$EventName,
        [Parameter(Mandatory = $true)][object[]]$Groups
    )
    $preserved = @()
    $currentGroups = $hooks[$EventName]
    if ($null -ne $currentGroups) {
        foreach ($group in @($currentGroups)) {
            if ($null -ne $group -and -not (Test-IsBridgeHookGroup $group)) {
                $preserved += $group
            }
        }
    }
    $hooks[$EventName] = @($preserved + $Groups)
}

Set-BridgeHookGroups "SessionStart" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand "session-start" 10 "登记 Codex 会话"))
)
Set-BridgeHookGroups "SessionEnd" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand "session-end" 3 "更新 Codex 会话"))
)
Set-BridgeHookGroups "PermissionRequest" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand "permission" 1500 "等待飞书审批"))
)
Set-BridgeHookGroups "Stop" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand "stop" 20 "通知飞书"))
)
Set-BridgeHookGroups "PreToolUse" @(
    (New-BridgeHookGroup "^request_user_input$" (New-BridgeHookCommand "request-user-input" 1500 "等待飞书补充信息")),
    (New-BridgeHookGroup ".*" (New-BridgeHookCommand "activity" 5 "同步 Codex 进度"))
)
Set-BridgeHookGroups "PostToolUse" @(
    (New-BridgeHookGroup ".*" (New-BridgeHookCommand "activity" 5 "同步 Codex 进度"))
)
Set-BridgeHookGroups "PreCompact" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand "activity" 5 "同步上下文压缩"))
)
Set-BridgeHookGroups "PostCompact" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand "activity" 5 "同步上下文压缩"))
)
Set-BridgeHookGroups "UserPromptSubmit" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand "activity" 5 "同步 Codex 任务开始"))
)

$config["description"] = "Route Codex approvals, questions, progress, and completion notifications through the local Feishu bridge."
$after = $config | ConvertTo-Json -Depth 30 -Compress
if ($before -ne $after -or -not (Test-Path -LiteralPath $hooksFile)) {
    New-Item -ItemType Directory -Path $hooksDirectory -Force | Out-Null
    $formatted = $config | ConvertTo-Json -Depth 30
    Set-Content -LiteralPath $hooksFile -Value $formatted -Encoding utf8
}
