[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$projectDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$desktopProjectPath = Join-Path $projectDirectory "desktop-control\AiCliFeishuControl.csproj"
$terminalProjectPath = Join-Path $projectDirectory "desktop-control\terminal-host\AiCliFeishuTerminalHost.csproj"
$hostProjectPath = Join-Path $projectDirectory "bridge-dotnet\src\AiCliFeishu.Bridge.Host\AiCliFeishu.Bridge.Host.csproj"

function Read-ProjectVersion {
    param([Parameter(Mandatory = $true)][string]$Path)

    [xml]$project = Get-Content -LiteralPath $Path -Raw
    $versionNode = $project.SelectSingleNode("/Project/PropertyGroup/Version")
    $value = if ($null -eq $versionNode) { "" } else { [string]$versionNode.InnerText }
    if ($value -notmatch '^\d+\.\d+\.\d+$') {
        throw "Project contains an invalid release version: $Path"
    }
    return $value
}

$version = Read-ProjectVersion $desktopProjectPath
foreach ($path in @($terminalProjectPath, $hostProjectPath)) {
    $componentVersion = Read-ProjectVersion $path
    if ($componentVersion -ne $version) {
        throw "Release component version mismatch: $path is $componentVersion, expected $version."
    }
}

$releaseDirectory = Join-Path $projectDirectory "release"
$releaseName = "ai-cli-feishu-bridge-v$version-windows-x64"
$stagingDirectory = Join-Path $releaseDirectory $releaseName
$zipPath = Join-Path $releaseDirectory "$releaseName.zip"
$hashPath = "$zipPath.sha256"
$publishDirectory = Join-Path $projectDirectory "desktop-control\publish"

function Assert-PathInside {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Directory
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escaped the expected directory: $fullPath"
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $Executable @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Executable failed with exit code $LASTEXITCODE."
        }
    } finally {
        Pop-Location
    }
}

Assert-PathInside $publishDirectory $projectDirectory
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

Invoke-Checked "dotnet.exe" @(
    "publish",
    ".\desktop-control\AiCliFeishuControl.csproj",
    "-c",
    "Release",
    "-o",
    ".\desktop-control\publish",
    "--nologo"
) $projectDirectory

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
foreach ($path in @($stagingDirectory, $zipPath, $hashPath)) {
    Assert-PathInside $path $releaseDirectory
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
foreach ($file in @(
    ".env.example",
    "LICENSE",
    "README.md",
    "README_EN.md"
)) {
    Copy-Item `
        -LiteralPath (Join-Path $projectDirectory $file) `
        -Destination (Join-Path $stagingDirectory $file)
}

$releaseScripts = Join-Path $stagingDirectory "scripts"
New-Item -ItemType Directory -Path $releaseScripts | Out-Null
foreach ($file in @(
    "install-hooks.ps1",
    "install-claude-code-hooks.ps1",
    "install-autostart.ps1",
    "uninstall-autostart.ps1"
)) {
    Copy-Item `
        -LiteralPath (Join-Path $PSScriptRoot $file) `
        -Destination (Join-Path $releaseScripts $file)
}

$executables = [ordered]@{
    "AiCliFeishuControl.exe" = "AI CLI飞书助手.exe"
    "AiCliFeishuTerminalHost.exe" = "AiCliFeishuTerminalHost.exe"
    "AiCliFeishuBridgeHost.exe" = "AiCliFeishuBridgeHost.exe"
}
foreach ($entry in $executables.GetEnumerator()) {
    $source = Join-Path $publishDirectory $entry.Key
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Published component is missing: $($entry.Key)"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stagingDirectory $entry.Value)
}

$requiredPaths = @(
    "AI CLI飞书助手.exe",
    "AiCliFeishuTerminalHost.exe",
    "AiCliFeishuBridgeHost.exe",
    "scripts\install-hooks.ps1",
    "scripts\install-claude-code-hooks.ps1"
)
foreach ($relativePath in $requiredPaths) {
    if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $relativePath) -PathType Leaf)) {
        throw "Release package is missing $relativePath."
    }
}

$expectedFileVersion = "$version.0"
foreach ($executableName in $executables.Values) {
    $executable = Get-Item -LiteralPath (Join-Path $stagingDirectory $executableName)
    if ($executable.VersionInfo.FileVersion -ne $expectedFileVersion) {
        throw "$executableName has version $($executable.VersionInfo.FileVersion), expected $expectedFileVersion."
    }
}

foreach ($forbiddenPath in @(
    ".env",
    "data",
    "dist",
    "node_modules",
    "package.json",
    "package-lock.json",
    "tsconfig.json",
    "AiCliFeishuControl.dll",
    "AiCliFeishuControl.deps.json",
    "AiCliFeishuControl.runtimeconfig.json",
    "AiCliFeishuTerminalHost.dll",
    "AiCliFeishuTerminalHost.deps.json",
    "AiCliFeishuTerminalHost.runtimeconfig.json",
    "AiCliFeishuBridgeHost.dll",
    "AiCliFeishuBridgeHost.deps.json",
    "AiCliFeishuBridgeHost.runtimeconfig.json"
)) {
    if (Test-Path -LiteralPath (Join-Path $stagingDirectory $forbiddenPath)) {
        throw "Release package unexpectedly contains $forbiddenPath."
    }
}
$scriptArtifacts = Get-ChildItem -LiteralPath $stagingDirectory -Recurse -File |
    Where-Object { $_.Extension -in @(".js", ".mjs", ".cjs", ".ts") }
if ($scriptArtifacts.Count -ne 0) {
    throw "Release package unexpectedly contains JavaScript or TypeScript artifacts."
}

[IO.Compression.ZipFile]::CreateFromDirectory(
    $stagingDirectory,
    $zipPath,
    [IO.Compression.CompressionLevel]::Optimal,
    $true)
$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
Set-Content `
    -LiteralPath $hashPath `
    -Value "$($hash.Hash)  $([IO.Path]::GetFileName($zipPath))" `
    -Encoding ascii

Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
Write-Output "Release ZIP: $zipPath"
Write-Output "SHA256: $($hash.Hash)"
