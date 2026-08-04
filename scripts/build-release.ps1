[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$projectDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$manifestPath = Join-Path $projectDirectory "package.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "package.json contains an invalid release version: $version"
}

$releaseDirectory = Join-Path $projectDirectory "release"
$releaseName = "codex-feishu-bridge-v$version-windows-x64"
$stagingDirectory = Join-Path $releaseDirectory $releaseName
$zipPath = Join-Path $releaseDirectory "$releaseName.zip"
$hashPath = "$zipPath.sha256"
$publishDirectory = Join-Path $projectDirectory "desktop-control\publish"

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

Invoke-Checked "npm.cmd" @("run", "build") $projectDirectory
Invoke-Checked "dotnet.exe" @(
    "publish",
    ".\desktop-control\CodexFeishuControl.csproj",
    "-c",
    "Release",
    "-o",
    ".\desktop-control\publish"
) $projectDirectory

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
foreach ($path in @($stagingDirectory, $zipPath, $hashPath)) {
    $fullPath = [IO.Path]::GetFullPath($path)
    $releasePrefix = $releaseDirectory.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release output escaped the release directory: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
foreach ($file in @(
    ".env.example",
    "LICENSE",
    "README.md",
    "README_EN.md",
    "package.json",
    "package-lock.json"
)) {
    Copy-Item `
        -LiteralPath (Join-Path $projectDirectory $file) `
        -Destination (Join-Path $stagingDirectory $file)
}

Copy-Item `
    -LiteralPath (Join-Path $projectDirectory "dist") `
    -Destination (Join-Path $stagingDirectory "dist") `
    -Recurse

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

Copy-Item `
    -LiteralPath (Join-Path $publishDirectory "CodexFeishuControl.exe") `
    -Destination (Join-Path $stagingDirectory "Codex飞书助手.exe")
Copy-Item `
    -LiteralPath (Join-Path $publishDirectory "CodexFeishuTerminalHost.exe") `
    -Destination (Join-Path $stagingDirectory "CodexFeishuTerminalHost.exe")

Invoke-Checked "npm.cmd" @(
    "ci",
    "--omit=dev",
    "--ignore-scripts",
    "--prefer-offline",
    "--no-audit",
    "--no-fund"
) $stagingDirectory

$requiredPaths = @(
    "Codex飞书助手.exe",
    "CodexFeishuTerminalHost.exe",
    "dist\index.js",
    "node_modules\@larksuiteoapi\node-sdk\package.json",
    "node_modules\dotenv\package.json",
    "scripts\install-hooks.ps1",
    "scripts\install-claude-code-hooks.ps1"
)
foreach ($relativePath in $requiredPaths) {
    if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $relativePath) -PathType Leaf)) {
        throw "Release package is missing $relativePath."
    }
}
$expectedFileVersion = "$version.0"
foreach ($executableName in @("Codex飞书助手.exe", "CodexFeishuTerminalHost.exe")) {
    $executable = Get-Item -LiteralPath (Join-Path $stagingDirectory $executableName)
    if ($executable.VersionInfo.FileVersion -ne $expectedFileVersion) {
        throw "$executableName has version $($executable.VersionInfo.FileVersion), expected $expectedFileVersion."
    }
}
foreach ($forbiddenPath in @(
    ".env",
    "data",
    "CODE-REVIEW-2026-08-04.md",
    "node_modules\tsx",
    "node_modules\typescript"
)) {
    if (Test-Path -LiteralPath (Join-Path $stagingDirectory $forbiddenPath)) {
        throw "Release package unexpectedly contains $forbiddenPath."
    }
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
