[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$projectDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$packageManifest = Get-Content -LiteralPath (Join-Path $projectDirectory "package.json") -Raw |
    ConvertFrom-Json
$version = [string]$packageManifest.version
$expectedFileVersion = "$version.0"
$verificationRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    "ai-cli-feishu-shadow-release-$([Guid]::NewGuid().ToString('N'))"
$publishDirectory = Join-Path $verificationRoot "publish"
$dataDirectory = Join-Path $verificationRoot "data"
$process = $null

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

function Get-FreeLoopbackPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    } finally {
        $listener.Stop()
    }
}

try {
    New-Item -ItemType Directory -Path $verificationRoot | Out-Null
    Invoke-Checked "dotnet.exe" @(
        "publish",
        ".\desktop-control\AiCliFeishuControl.csproj",
        "-c",
        "Release",
        "-o",
        $publishDirectory
    ) $projectDirectory

    $requiredExecutables = @(
        "AiCliFeishuControl.exe",
        "AiCliFeishuTerminalHost.exe",
        "AiCliFeishuBridgeHost.exe"
    )
    foreach ($executableName in $requiredExecutables) {
        $executablePath = Join-Path $publishDirectory $executableName
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "Desktop publish is missing $executableName."
        }
        $executable = Get-Item -LiteralPath $executablePath
        if ($executable.VersionInfo.FileVersion -ne $expectedFileVersion) {
            throw "$executableName has version $($executable.VersionInfo.FileVersion), expected $expectedFileVersion."
        }
    }

    foreach ($forbiddenName in @(
        "AiCliFeishuTerminalHost.dll",
        "AiCliFeishuTerminalHost.deps.json",
        "AiCliFeishuTerminalHost.runtimeconfig.json",
        "AiCliFeishuTerminalHost.pdb",
        "AiCliFeishuBridgeHost.dll",
        "AiCliFeishuBridgeHost.deps.json",
        "AiCliFeishuBridgeHost.runtimeconfig.json",
        "AiCliFeishuBridgeHost.pdb"
    )) {
        if (Test-Path -LiteralPath (Join-Path $publishDirectory $forbiddenName)) {
            throw "Desktop publish unexpectedly contains $forbiddenName."
        }
    }

    $port = Get-FreeLoopbackPort
    $hostExecutable = Join-Path $publishDirectory "AiCliFeishuBridgeHost.exe"
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $hostExecutable
    $startInfo.WorkingDirectory = $publishDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major"
    foreach ($argument in @(
        "--data-directory", $dataDirectory,
        "--listen", "127.0.0.1",
        "--port", [string]$port,
        "--ownership", "passive",
        "--instance", "release-verification"
    )) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Failed to start AiCliFeishuBridgeHost.exe."
    }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    $health = $null
    $lastHealthError = $null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            throw "Bridge Host exited before becoming healthy with code $($process.ExitCode). stdout=$stdout stderr=$stderr"
        }
        try {
            $health = Invoke-RestMethod `
                -Uri "http://127.0.0.1:$port/health" `
                -TimeoutSec 2
            break
        } catch {
            $lastHealthError = $_
            Start-Sleep -Milliseconds 250
        }
    }
    if ($null -eq $health) {
        throw "Bridge Host health check timed out: $lastHealthError"
    }
    if ($health.ok -ne $true) {
        throw "Bridge Host public health response was not OK."
    }

    Write-Output "Verified desktop publish: $publishDirectory"
    Write-Output "Verified Bridge Host version: $expectedFileVersion"
    Write-Output "Verified passive Bridge Host health on loopback port $port"
} finally {
    $processCleanupError = $null
    if ($null -ne $process) {
        try {
            if (-not $process.HasExited) {
                $process.Kill($true)
            }
            if (-not $process.WaitForExit(10000)) {
                $processCleanupError = "Bridge Host verification process did not exit."
            }
        } catch {
            $processCleanupError = "Bridge Host verification process cleanup failed: $($_.Exception.Message)"
        } finally {
            $process.Dispose()
        }
    }

    $resolvedVerificationRoot = [IO.Path]::GetFullPath($verificationRoot)
    $temporaryDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedVerificationRoot.StartsWith(
        $temporaryDirectory,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Verification output escaped the temporary directory: $resolvedVerificationRoot"
    }
    if (Test-Path -LiteralPath $resolvedVerificationRoot) {
        Remove-Item -LiteralPath $resolvedVerificationRoot -Recurse -Force
    }
    if ($null -ne $processCleanupError) {
        throw $processCleanupError
    }
}
