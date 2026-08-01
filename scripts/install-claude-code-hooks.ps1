[CmdletBinding()]
param(
    [ValidateRange(0, 65535)]
    [int]$Port = 0
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent $PSScriptRoot
$normalizedProject = $projectDirectory.Replace("\", "/")
$claudeDirectory = Join-Path $env:USERPROFILE ".claude"
$settingsFile = Join-Path $claudeDirectory "settings.json"

# Read existing settings or create empty config
if (Test-Path -LiteralPath $settingsFile) {
    $raw = Get-Content -LiteralPath $settingsFile -Raw
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
        [Parameter(Mandatory = $true)][int]$Timeout
    )
    $command = "node `"$normalizedProject/dist/hooks/$ScriptName.js`""
    return [ordered]@{
        type = "command"
        command = $command
        timeout = $Timeout
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
    # Remove both the current grouped format and the unfinished legacy direct format.
    $directCommand = [string]$Group["command"]
    if ($directCommand -match 'codex-feishu-bridge[\\/]dist[\\/]hooks[\\/]') {
        return $true
    }
    foreach ($hook in @($Group["hooks"])) {
        if ($null -eq $hook -or $hook -isnot [System.Collections.IDictionary]) {
            continue
        }
        $command = [string]$hook["command"]
        if ($command -match 'codex-feishu-bridge[\\/]dist[\\/]hooks[\\/]') {
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
    $hooks[$EventName] = @($Groups + $preserved)
}

Set-BridgeHookGroups "SessionStart" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand "claude-code-session-start" 10))
)
Set-BridgeHookGroups "SessionEnd" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand "claude-code-session-end" 3))
)
Set-BridgeHookGroups "PermissionRequest" @(
    (New-BridgeHookGroup ".*" (New-BridgeHookCommand "claude-code-permission-request" 1500))
)
Set-BridgeHookGroups "Stop" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand "claude-code-stop" 20))
)
Set-BridgeHookGroups "PreToolUse" @(
    (New-BridgeHookGroup ".*" (New-BridgeHookCommand "claude-code-pre-tool-use" 1500))
)
Set-BridgeHookGroups "PostToolUse" @(
    (New-BridgeHookGroup ".*" (New-BridgeHookCommand "claude-code-post-tool-use" 5))
)
Set-BridgeHookGroups "UserPromptSubmit" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand "claude-code-user-prompt-submit" 5))
)

$after = $config | ConvertTo-Json -Depth 30 -Compress
if ($before -ne $after -or -not (Test-Path -LiteralPath $settingsFile)) {
    New-Item -ItemType Directory -Path $claudeDirectory -Force | Out-Null
    $formatted = $config | ConvertTo-Json -Depth 30
    Set-Content -LiteralPath $settingsFile -Value $formatted -Encoding utf8
    Write-Host "Claude Code hooks installed to: $settingsFile"
} else {
    Write-Host "Claude Code hooks already up to date."
}
