[CmdletBinding()]
param(
    [ValidateRange(0, 65535)]
    [int]$Port = 0,
    [string]$SourceHookHost = "",
    [string]$StableHookDirectory = ""
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent $PSScriptRoot
$normalizedProject = $projectDirectory.Replace("\", "/")
$sourceHookHost = if ([string]::IsNullOrWhiteSpace($SourceHookHost)) {
    Join-Path $projectDirectory "AiCliFeishuTerminalHost.exe"
} else {
    [IO.Path]::GetFullPath($SourceHookHost)
}
if (-not (Test-Path -LiteralPath $sourceHookHost -PathType Leaf)) {
    throw "C# Hook Host was not found: $sourceHookHost"
}
$bridgeUrl = if ($Port -gt 0) { "http://127.0.0.1:$Port" } else { "http://127.0.0.1:8765" }
$hookRelayDirectory = if ([string]::IsNullOrWhiteSpace($StableHookDirectory)) {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw "LOCALAPPDATA is unavailable; the stable Hook Relay cannot be installed."
    }
    Join-Path $localAppData "AiCliFeishu\hooks"
} else {
    [IO.Path]::GetFullPath($StableHookDirectory)
}
$hookHostPath = Join-Path $hookRelayDirectory "AiCliFeishuTerminalHost.exe"
$hookLauncherPath = Join-Path $hookRelayDirectory "AiCliFeishuHook.cmd"
$hookConfigPath = Join-Path $hookRelayDirectory "active-install.json"

function Remove-RelayFileBestEffort {
    param([Parameter(Mandatory = $true)][string]$Path)

    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        if (-not (Test-Path -LiteralPath $Path)) {
            return
        }
        try {
            Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
            return
        } catch {
            if ($attempt -lt 3) {
                Start-Sleep -Milliseconds (50 * ($attempt + 1))
            }
        }
    }
}

function Replace-RelayFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $backup = "$Destination.$PID.$([Guid]::NewGuid().ToString('N')).backup"
        try {
            [IO.File]::Replace($Source, $Destination, $backup)
        } finally {
            Remove-RelayFileBestEffort $backup
        }
    } else {
        [IO.File]::Move($Source, $Destination)
    }
}

function Enter-ExclusiveConfigLock {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$TimeoutMilliseconds = 5000
    )
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ($true) {
        try {
            return [IO.File]::Open(
                $Path,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
        } catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "Timed out waiting for the Claude Code settings lock: $Path"
            }
            Start-Sleep -Milliseconds 50
        }
    }
}

function Write-AtomicJsonConfig {
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$StableBackup
    )
    $directory = [IO.Path]::GetDirectoryName($Destination)
    $temporary = Join-Path $directory (
        "$([IO.Path]::GetFileName($Destination)).$PID.$([Guid]::NewGuid().ToString('N')).tmp")
    $replacementBackup = "$Destination.$PID.$([Guid]::NewGuid().ToString('N')).backup"
    try {
        [IO.File]::WriteAllText(
            $temporary,
            $Content,
            [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Destination -PathType Leaf) {
            [IO.File]::Replace($temporary, $Destination, $replacementBackup)
            [IO.File]::Copy($replacementBackup, $StableBackup, $true)
        } else {
            [IO.File]::Move($temporary, $Destination)
        }
    } finally {
        Remove-RelayFileBestEffort $temporary
        Remove-RelayFileBestEffort $replacementBackup
    }
}

function Install-StableHookRelay {
    New-Item -ItemType Directory -Path $hookRelayDirectory -Force | Out-Null

    foreach ($pattern in @(
        "AiCliFeishuTerminalHost.exe.*.backup",
        "AiCliFeishuHook.cmd.*.backup",
        "active-install.json.*.backup"
    )) {
        foreach ($backup in @(Get-ChildItem `
            -LiteralPath $hookRelayDirectory `
            -Filter $pattern `
            -File `
            -ErrorAction SilentlyContinue)) {
            Remove-RelayFileBestEffort $backup.FullName
        }
    }

    $copyRequired = -not (Test-Path -LiteralPath $hookHostPath -PathType Leaf)
    if (-not $copyRequired) {
        $copyRequired = (Get-FileHash -LiteralPath $sourceHookHost -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $hookHostPath -Algorithm SHA256).Hash
    }
    if ($copyRequired) {
        $temporaryHost = Join-Path $hookRelayDirectory (
            "AiCliFeishuTerminalHost.$PID.$([Guid]::NewGuid().ToString('N')).tmp")
        try {
            Copy-Item -LiteralPath $sourceHookHost -Destination $temporaryHost -Force
            Replace-RelayFile $temporaryHost $hookHostPath
        } finally {
            if (Test-Path -LiteralPath $temporaryHost) {
                Remove-Item -LiteralPath $temporaryHost -Force
            }
        }
    }

    $launcherContent = @'
@echo off
"%~dp0AiCliFeishuTerminalHost.exe" %*
exit /b 0
'@
    $currentLauncher = if (Test-Path -LiteralPath $hookLauncherPath -PathType Leaf) {
        Get-Content -LiteralPath $hookLauncherPath -Raw
    } else {
        ""
    }
    if ($currentLauncher.Trim() -ne $launcherContent.Trim()) {
        $temporaryLauncher = "$hookLauncherPath.$PID.tmp"
        try {
            [IO.File]::WriteAllText(
                $temporaryLauncher,
                $launcherContent,
                [Text.UTF8Encoding]::new($false))
            Replace-RelayFile $temporaryLauncher $hookLauncherPath
        } finally {
            if (Test-Path -LiteralPath $temporaryLauncher) {
                Remove-Item -LiteralPath $temporaryLauncher -Force
            }
        }
    }

    $locatorJson = [ordered]@{
        schemaVersion = 1
        bridgeRoot = $projectDirectory
        bridgeUrl = $bridgeUrl
    } | ConvertTo-Json -Depth 5
    $currentLocator = if (Test-Path -LiteralPath $hookConfigPath -PathType Leaf) {
        Get-Content -LiteralPath $hookConfigPath -Raw
    } else {
        ""
    }
    if ($currentLocator.Trim() -ne $locatorJson.Trim()) {
        $temporaryConfig = "$hookConfigPath.$PID.tmp"
        try {
            Set-Content -LiteralPath $temporaryConfig -Value $locatorJson -Encoding utf8
            Replace-RelayFile $temporaryConfig $hookConfigPath
        } finally {
            if (Test-Path -LiteralPath $temporaryConfig) {
                Remove-Item -LiteralPath $temporaryConfig -Force
            }
        }
    }
}

Install-StableHookRelay
$hookLauncher = $hookLauncherPath.Replace("\", "/")
$hookConfig = $hookConfigPath.Replace("\", "/")
$claudeDirectory = Join-Path $env:USERPROFILE ".claude"
$settingsFile = Join-Path $claudeDirectory "settings.json"
$settingsLockFile = Join-Path $claudeDirectory "settings.json.ai-cli-feishu.lock"
$settingsBackupFile = Join-Path $claudeDirectory "settings.json.ai-cli-feishu.backup"

New-Item -ItemType Directory -Path $claudeDirectory -Force | Out-Null
$settingsLock = Enter-ExclusiveConfigLock $settingsLockFile
try {

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
        [Parameter(Mandatory = $true)][int]$Timeout
    )
    # Claude Code 在 Windows 上通过 bash 执行 Hook 命令，bash 会重新解析引号，
    # 因此不能像 Codex 那样套一层 cmd.exe /d /s /c "…"：外层引号会被 bash 吃掉，
    # cmd.exe 取不到 /c 的命令而进入交互模式，把 stdin 的 Hook JSON 当成命令。
    $command =
        "`"$hookLauncher`" --bridge-hook claudecode " +
        "--bridge-config `"$hookConfig`""
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
    function Test-IsOwnedCommand([object]$Value) {
        $command = ([string]$Value).Replace('\', '/')
        if ($command -match '(?:AiCliFeishuTerminalHost\.exe|AiCliFeishuHook\.cmd)[^\r\n]*--bridge-hook\s+claudecode\s+') {
            return $true
        }
        if ($command -match '/dist/hooks/claude-code-(?:permission-request|activity|post-tool-use|pre-tool-use|session-end|session-start|stop|user-prompt-submit)\.js(?:[\s"''\r\n]|$)') {
            return $true
        }
        return $command.IndexOf(
            $normalizedProject,
            [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $command.IndexOf('/hooks/', [StringComparison]::OrdinalIgnoreCase) -ge 0
    }
    $directCommand = ([string]$Group["command"]).Replace('\', '/')
    if (Test-IsOwnedCommand $directCommand) {
        return $true
    }
    foreach ($hook in @($Group["hooks"])) {
        if ($null -eq $hook -or $hook -isnot [System.Collections.IDictionary]) {
            continue
        }
        $command = ([string]$hook["command"]).Replace('\', '/')
        if (Test-IsOwnedCommand $command) {
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
    (New-BridgeHookGroup "" (New-BridgeHookCommand 10))
)
Set-BridgeHookGroups "SessionEnd" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand 3))
)
Set-BridgeHookGroups "PermissionRequest" @(
    (New-BridgeHookGroup ".*" (New-BridgeHookCommand 1500))
)
Set-BridgeHookGroups "Stop" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand 20))
)
Set-BridgeHookGroups "PreToolUse" @(
    (New-BridgeHookGroup ".*" (New-BridgeHookCommand 1500))
)
Set-BridgeHookGroups "PostToolUse" @(
    (New-BridgeHookGroup ".*" (New-BridgeHookCommand 5))
)
Set-BridgeHookGroups "PostToolUseFailure" @(
    (New-BridgeHookGroup ".*" (New-BridgeHookCommand 5))
)
Set-BridgeHookGroups "PreCompact" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand 5))
)
Set-BridgeHookGroups "PostCompact" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand 5))
)
Set-BridgeHookGroups "UserPromptSubmit" @(
    (New-BridgeHookGroup "" (New-BridgeHookCommand 5))
)

$after = $config | ConvertTo-Json -Depth 30 -Compress
if ($before -ne $after -or -not (Test-Path -LiteralPath $settingsFile)) {
    $formatted = $config | ConvertTo-Json -Depth 30
    Write-AtomicJsonConfig $settingsFile $formatted $settingsBackupFile
    Write-Host "Claude Code hooks installed to: $settingsFile"
} else {
    Write-Host "Claude Code hooks already up to date."
}
} finally {
    if ($null -ne $settingsLock) {
        $settingsLock.Dispose()
    }
    Remove-RelayFileBestEffort $settingsLockFile
}
