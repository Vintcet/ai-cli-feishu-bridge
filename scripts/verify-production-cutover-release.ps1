[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "Production cutover verification requires PowerShell 7 or later."
}

$projectDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$packageManifest = Get-Content -LiteralPath (Join-Path $projectDirectory "package.json") -Raw |
    ConvertFrom-Json
$version = [string]$packageManifest.version
$expectedFileVersion = "$version.0"
$verificationRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    "ai-cli-feishu-production-cutover-$([Guid]::NewGuid().ToString('N'))"
$publishDirectory = Join-Path $verificationRoot "publish"
$desktopExecutable = Join-Path $publishDirectory "AiCliFeishuControl.exe"
$dotNetHostExecutable = Join-Path $publishDirectory "AiCliFeishuBridgeHost.exe"
$controlToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
$fakeAppId = "cli_isolated_cutover"
$fakeAppSecret = "isolated-cutover-secret"
$nodeExecutable = (Get-Command node.exe -ErrorAction Stop).Source
$scenarios = [Collections.Generic.List[object]]::new()
$proxyRun = $null
$verificationError = $null

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
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

function Get-FreeLoopbackPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    } finally {
        $listener.Stop()
    }
}

function Test-ProcessAlive {
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        $process.Dispose()
        return $true
    } catch [Management.Automation.ActionPreferenceStopException] {
        return $false
    }
}

function Start-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][hashtable]$Environment,
        [bool]$RedirectOutput = $true
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $RedirectOutput
    $startInfo.RedirectStandardError = $RedirectOutput
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $startInfo.Environment[[string]$entry.Key] = [string]$entry.Value
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        $process.Dispose()
        throw "Failed to start $FileName."
    }
    return [pscustomobject]@{
        Process = $process
        Stdout = if ($RedirectOutput) {
            $process.StandardOutput.ReadToEndAsync()
        } else {
            $null
        }
        Stderr = if ($RedirectOutput) {
            $process.StandardError.ReadToEndAsync()
        } else {
            $null
        }
    }
}

function Wait-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not $Run.Process.WaitForExit($TimeoutSeconds * 1000)) {
        try {
            $Run.Process.Kill($true)
            $null = $Run.Process.WaitForExit(10000)
        } catch {
        }
        throw "$Label did not exit within $TimeoutSeconds seconds."
    }
    $result = [pscustomobject]@{
        ExitCode = $Run.Process.ExitCode
        Output = if ($null -ne $Run.Stdout) {
            $Run.Stdout.GetAwaiter().GetResult()
        } else {
            ""
        }
        Error = if ($null -ne $Run.Stderr) {
            $Run.Stderr.GetAwaiter().GetResult()
        } else {
            ""
        }
    }
    $Run.Process.Dispose()
    return $result
}

function Start-RejectProxy {
    param([Parameter(Mandatory = $true)][int]$Port)

    $code = @"
const net = require('node:net');
const server = net.createServer(socket => socket.destroy());
server.listen($Port, '127.0.0.1');
"@
    $run = Start-CapturedProcess `
        -FileName $nodeExecutable `
        -Arguments @("-e", $code) `
        -WorkingDirectory $verificationRoot `
        -Environment @{}

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($run.Process.HasExited) {
            $result = Wait-CapturedProcess $run 1 "Reject proxy"
            throw "Reject proxy exited early. stdout=$($result.Output) stderr=$($result.Error)"
        }
        $client = [Net.Sockets.TcpClient]::new()
        try {
            $client.Connect("127.0.0.1", $Port)
            return $run
        } catch [Net.Sockets.SocketException] {
        } finally {
            $client.Dispose()
        }
        Start-Sleep -Milliseconds 50
    }
    throw "Reject proxy did not start on loopback port $Port."
}

function New-IsolatedScenario {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ProxyUri
    )

    $root = Join-Path $verificationRoot $Name
    $dataDirectory = Join-Path $root "data"
    $workspaceDirectory = Join-Path $root "workspace"
    $distDirectory = Join-Path $root "dist"
    $nodeModulesLink = Join-Path $root "node_modules"
    $port = Get-FreeLoopbackPort
    New-Item -ItemType Directory -Path $root, $dataDirectory, $workspaceDirectory | Out-Null
    Copy-Item `
        -LiteralPath (Join-Path $projectDirectory "package.json") `
        -Destination (Join-Path $root "package.json")
    Copy-Item `
        -LiteralPath (Join-Path $projectDirectory "dist") `
        -Destination $distDirectory `
        -Recurse
    New-Item `
        -ItemType Junction `
        -Path $nodeModulesLink `
        -Target (Join-Path $projectDirectory "node_modules") | Out-Null

    $environmentFile = @(
        "FEISHU_APP_ID=$fakeAppId"
        "FEISHU_APP_SECRET=$fakeAppSecret"
        "BRIDGE_HTTP_PORT=$port"
        "DEFAULT_WORKSPACE_ROOT=$workspaceDirectory"
        "OPENCODE_AUTO_DISCOVER=0"
        "HTTP_PROXY=$ProxyUri"
        "HTTPS_PROXY=$ProxyUri"
        "ALL_PROXY=$ProxyUri"
        "NO_PROXY=127.0.0.1,localhost"
    ) -join "`n"
    [IO.File]::WriteAllText(
        (Join-Path $root ".env"),
        $environmentFile + "`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $dataDirectory "control-token.json"),
        (([ordered]@{ token = $controlToken } | ConvertTo-Json -Compress) + "`n"),
        [Text.UTF8Encoding]::new($false))

    $environment = @{
        AI_CLI_FEISHU_BRIDGE_ROOT = $root
        AI_CLI_FEISHU_BRIDGE_HOST = "node"
        AI_CLI_FEISHU_DOTNET_HOST_PATH = $dotNetHostExecutable
        FEISHU_APP_ID = $fakeAppId
        FEISHU_APP_SECRET = $fakeAppSecret
        BRIDGE_HTTP_PORT = [string]$port
        DEFAULT_WORKSPACE_ROOT = $workspaceDirectory
        OPENCODE_AUTO_DISCOVER = "0"
        AI_CLI_FEISHU_MIGRATION_RECORDING = "0"
        HTTP_PROXY = $ProxyUri
        HTTPS_PROXY = $ProxyUri
        ALL_PROXY = $ProxyUri
        NO_PROXY = "127.0.0.1,localhost"
        DOTNET_ROLL_FORWARD = "Major"
    }
    $scenario = [pscustomobject]@{
        Name = $Name
        Root = [IO.Path]::GetFullPath($root)
        DataDirectory = [IO.Path]::GetFullPath($dataDirectory)
        NodeModulesLink = [IO.Path]::GetFullPath($nodeModulesLink)
        Port = $port
        Endpoint = "http://127.0.0.1:$port/"
        Environment = $environment
        KnownProcessIds = [Collections.Generic.HashSet[int]]::new()
        InitialNodeRun = $null
    }
    $scenarios.Add($scenario)
    return $scenario
}

function Get-AuthenticatedStatus {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$Path
    )

    try {
        return Invoke-RestMethod `
            -Uri ($Scenario.Endpoint + $Path) `
            -Headers @{ "X-AI-CLI-Feishu-Control-Token" = $controlToken } `
            -NoProxy `
            -TimeoutSec 2
    } catch {
        return $null
    }
}

function Assert-HostIdentity {
    param(
        [Parameter(Mandatory = $true)]$Status,
        [Parameter(Mandatory = $true)][string]$HostKind,
        [Parameter(Mandatory = $true)][string]$InstanceName,
        [int]$ExpectedProcessId = 0
    )

    Assert-Condition ($Status.ok -eq $true) "Authenticated Host status was not OK."
    Assert-Condition ([string]$Status.hostKind -ceq $HostKind) "Authenticated Host kind mismatch."
    Assert-Condition ([int]$Status.managementApiVersion -eq 1) "Management API version mismatch."
    Assert-Condition ([string]$Status.ownershipMode -ceq "active") "Ownership mode mismatch."
    Assert-Condition ($Status.activeOwner -eq $true) "Authenticated Host was not Active Owner."
    Assert-Condition ([string]$Status.instanceName -ceq $InstanceName) "Host instance mismatch."
    Assert-Condition ([int]$Status.processId -gt 0) "Authenticated Host PID was invalid."
    if ($ExpectedProcessId -gt 0) {
        Assert-Condition `
            ([int]$Status.processId -eq $ExpectedProcessId) `
            "Authenticated Host PID changed unexpectedly."
    }
}

function Wait-AuthenticatedHost {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$HostKind,
        [Parameter(Mandatory = $true)][string]$InstanceName,
        [int]$ExpectedProcessId = 0,
        [int]$TimeoutSeconds = 30
    )

    $path = if ($HostKind -ceq "dotnet") { "control/status" } else { "health" }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $status = Get-AuthenticatedStatus $Scenario $path
        if ($null -ne $status) {
            try {
                Assert-HostIdentity $status $HostKind $InstanceName $ExpectedProcessId
                [void]$Scenario.KnownProcessIds.Add([int]$status.processId)
                return $status
            } catch {
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for authenticated $HostKind/$InstanceName Host."
}

function Test-PublicEndpointAlive {
    param([Parameter(Mandatory = $true)]$Scenario)

    try {
        $null = Invoke-WebRequest `
            -Uri ($Scenario.Endpoint + "health") `
            -NoProxy `
            -TimeoutSec 1
        return $true
    } catch {
        return $false
    }
}

function Stop-AuthenticatedHost {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$HostKind,
        [Parameter(Mandatory = $true)][string]$InstanceName,
        [Parameter(Mandatory = $true)][int]$ProcessId
    )

    $null = Wait-AuthenticatedHost `
        $Scenario `
        $HostKind `
        $InstanceName `
        $ProcessId `
        5
    $headers = @{
        "X-AI-CLI-Feishu-Control-Token" = $controlToken
        "X-AI-CLI-Feishu-Expected-Host-Kind" = $HostKind
        "X-AI-CLI-Feishu-Management-Api-Version" = "1"
        "X-AI-CLI-Feishu-Expected-Process-Id" = [string]$ProcessId
    }
    $response = Invoke-WebRequest `
        -Uri ($Scenario.Endpoint + "control/shutdown") `
        -Method Post `
        -Headers $headers `
        -ContentType "application/json" `
        -Body "{}" `
        -NoProxy `
        -TimeoutSec 5
    Assert-Condition `
        ($response.StatusCode -eq 202) `
        "Authenticated Host did not accept shutdown."

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (-not (Test-ProcessAlive $ProcessId) -and
            -not (Test-PublicEndpointAlive $Scenario)) {
            return
        }
        $replacement = Get-AuthenticatedStatus $Scenario "health"
        if ($null -ne $replacement -and
            [int]$replacement.processId -ne $ProcessId) {
            throw "A replacement Host appeared while stopping the authenticated Host."
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Authenticated Host did not stop cleanly."
}

function Start-InitialNodeHost {
    param([Parameter(Mandatory = $true)]$Scenario)

    $run = Start-CapturedProcess `
        -FileName $nodeExecutable `
        -Arguments @(Join-Path $Scenario.Root "dist\index.js") `
        -WorkingDirectory $Scenario.Root `
        -Environment $Scenario.Environment
    $Scenario.InitialNodeRun = $run
    [void]$Scenario.KnownProcessIds.Add($run.Process.Id)
    return Wait-AuthenticatedHost `
        $Scenario `
        "node" `
        "production" `
        $run.Process.Id `
        30
}

function Start-DesktopCommand {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    return Start-CapturedProcess `
        -FileName $desktopExecutable `
        -Arguments $Arguments `
        -WorkingDirectory $publishDirectory `
        -Environment $Scenario.Environment `
        -RedirectOutput $false
}

function Invoke-DesktopCommand {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int]$TimeoutSeconds = 90
    )

    $run = Start-DesktopCommand $Scenario $Arguments
    return Wait-CapturedProcess $run $TimeoutSeconds "Desktop command"
}

function Read-Checkpoint {
    param([Parameter(Mandatory = $true)]$Scenario)

    $path = Join-Path $Scenario.DataDirectory "bridge-host-cutover.checkpoint.json"
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Wait-CheckpointStage {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$Stage,
        [int]$TimeoutSeconds = 30
    )

    $path = Join-Path $Scenario.DataDirectory "bridge-host-cutover.checkpoint.json"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            try {
                $checkpoint = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                if ([string]$checkpoint.stage -ceq $Stage) {
                    return $checkpoint
                }
            } catch {
            }
        }
        Start-Sleep -Milliseconds 5
    }
    throw "Timed out waiting for checkpoint stage $Stage."
}

function Assert-OwnerLease {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$HostKind,
        [Parameter(Mandatory = $true)][string]$InstanceName,
        [Parameter(Mandatory = $true)][int]$ProcessId
    )

    $ownerPath = Join-Path `
        $Scenario.DataDirectory `
        "bridge-active-owner.lock\owner.json"
    $owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
    Assert-Condition ([int]$owner.schemaVersion -eq 1) "Owner lease schema mismatch."
    Assert-Condition ([string]$owner.hostKind -ceq $HostKind) "Owner lease Host kind mismatch."
    Assert-Condition ([string]$owner.ownershipMode -ceq "active") "Owner lease mode mismatch."
    Assert-Condition ([int]$owner.processId -eq $ProcessId) "Owner lease PID mismatch."
    Assert-Condition ([string]$owner.instanceName -ceq $InstanceName) "Owner lease instance mismatch."
    Assert-Condition `
        (-not [string]::IsNullOrWhiteSpace([string]$owner.leaseId)) `
        "Owner lease ID was missing."
}

function Assert-NoOwnerLease {
    param([Parameter(Mandatory = $true)]$Scenario)

    Assert-Condition `
        (-not (Test-Path -LiteralPath (Join-Path $Scenario.DataDirectory "bridge-active-owner.lock"))) `
        "Active Owner lease was not released."
}

function Get-ProcessCommandLine {
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId"
    if ($null -eq $process -or [string]::IsNullOrWhiteSpace([string]$process.CommandLine)) {
        throw "Could not inspect the launched Host command line."
    }
    return [string]$process.CommandLine
}

function Assert-DotNetControlSurfaces {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][int]$ProcessId
    )

    $status = Wait-AuthenticatedHost `
        $Scenario `
        "dotnet" `
        "production-dotnet" `
        $ProcessId `
        30
    Assert-Condition `
        ([string]$status.lifecycle -ceq "ready") `
        "C# control status was not ready."
    Assert-Condition `
        ([string]$status.store.status -ceq "loaded") `
        "C# production Store was not loaded."
    $health = Get-AuthenticatedStatus $Scenario "health"
    Assert-Condition ($null -ne $health) "Authenticated C# health was unavailable."
    Assert-HostIdentity $health "dotnet" "production-dotnet" $ProcessId
    return $status
}

function Invoke-SuccessfulCutoverScenario {
    param([Parameter(Mandatory = $true)]$Scenario)

    Write-Output "Running isolated successful production cutover..."
    $node = Start-InitialNodeHost $Scenario
    $nodeProcessId = [int]$node.processId
    Assert-OwnerLease $Scenario "node" "production" $nodeProcessId

    $cutover = Invoke-DesktopCommand `
        $Scenario `
        @("--bridge-cutover-to-dotnet", "--confirm-production-cutover") `
        120
    if ($cutover.ExitCode -ne 0) {
        throw "Desktop cutover failed. stdout=$($cutover.Output) stderr=$($cutover.Error)"
    }
    Assert-Condition `
        (-not (Test-ProcessAlive $nodeProcessId)) `
        "The authenticated Node Host did not exit during cutover."

    $dotNet = Wait-AuthenticatedHost $Scenario "dotnet" "production-dotnet" 0 30
    $dotNetProcessId = [int]$dotNet.processId
    $null = Assert-DotNetControlSurfaces $Scenario $dotNetProcessId
    Assert-OwnerLease $Scenario "dotnet" "production-dotnet" $dotNetProcessId

    $checkpointPath = Join-Path `
        $Scenario.DataDirectory `
        "bridge-host-cutover.checkpoint.json"
    $checkpointBytes = [IO.File]::ReadAllBytes($checkpointPath)
    $checkpoint = Read-Checkpoint $Scenario
    Assert-Condition ([int]$checkpoint.schemaVersion -eq 1) "Cutover checkpoint schema mismatch."
    Assert-Condition ([string]$checkpoint.stage -ceq "Completed") "Cutover checkpoint was not Completed."
    Assert-Condition ($checkpoint.requiresRollback -eq $false) "Completed checkpoint still required rollback."
    Assert-Condition ([string]$checkpoint.failureReason -ceq "None") "Completed checkpoint retained a failure reason."
    Assert-Condition `
        (-not [string]::IsNullOrWhiteSpace([string]$checkpoint.operationId)) `
        "Completed checkpoint operation ID was missing."
    Assert-Condition ([int]$checkpoint.expectedNode.processId -eq $nodeProcessId) "Checkpoint lost the authenticated Node PID."
    Assert-Condition ([string]$checkpoint.expectedNode.hostKind -ceq "node") "Checkpoint Node kind mismatch."
    Assert-Condition ([int]$checkpoint.expectedNode.managementApiVersion -eq 1) "Checkpoint Node API version mismatch."
    Assert-Condition ([string]$checkpoint.expectedNode.ownershipMode -ceq "active") "Checkpoint Node ownership mode mismatch."
    Assert-Condition ($checkpoint.expectedNode.activeOwner -eq $true) "Checkpoint Node was not Active Owner."
    Assert-Condition ([string]$checkpoint.expectedNode.instanceName -ceq "production") "Checkpoint Node instance mismatch."
    Assert-Condition ([int]$checkpoint.dotNetProcessId -eq $dotNetProcessId) "Checkpoint C# PID mismatch."
    Assert-Condition ([int]$checkpoint.nodeRollbackProcessId -eq 0) "Completed checkpoint unexpectedly recorded a Node rollback PID."
    Assert-Condition ([string]$checkpoint.expectedDotNetInstanceName -ceq "production-dotnet") "Checkpoint C# instance mismatch."

    $commandLine = Get-ProcessCommandLine $dotNetProcessId
    Assert-Condition `
        ($commandLine.Contains("--cutover-operation", [StringComparison]::Ordinal)) `
        "C# Host command line did not contain --cutover-operation."
    Assert-Condition `
        ($commandLine.Contains([string]$checkpoint.operationId, [StringComparison]::Ordinal)) `
        "C# Host command line did not bind the durable operation ID."

    Stop-AuthenticatedHost $Scenario "dotnet" "production-dotnet" $dotNetProcessId
    Assert-NoOwnerLease $Scenario

    $recovery = Invoke-DesktopCommand $Scenario @("--bridge-service") 120
    if ($recovery.ExitCode -ne 0) {
        throw "Desktop startup recovery failed. stdout=$($recovery.Output) stderr=$($recovery.Error)"
    }
    $recovered = Wait-AuthenticatedHost $Scenario "dotnet" "production-dotnet" 0 30
    $recoveredProcessId = [int]$recovered.processId
    Assert-Condition `
        ($recoveredProcessId -ne $dotNetProcessId) `
        "Completed recovery did not start a new C# Host process."
    $null = Assert-DotNetControlSurfaces $Scenario $recoveredProcessId
    Assert-OwnerLease $Scenario "dotnet" "production-dotnet" $recoveredProcessId
    Assert-Condition `
        ([Convert]::ToBase64String([IO.File]::ReadAllBytes($checkpointPath)) -ceq
            [Convert]::ToBase64String($checkpointBytes)) `
        "Completed startup recovery rewrote the durable checkpoint."
    $recoveredCommandLine = Get-ProcessCommandLine $recoveredProcessId
    Assert-Condition `
        ($recoveredCommandLine.Contains([string]$checkpoint.operationId, [StringComparison]::Ordinal)) `
        "Recovered C# Host did not reuse the durable operation ID."

    Stop-AuthenticatedHost $Scenario "dotnet" "production-dotnet" $recoveredProcessId
    Assert-NoOwnerLease $Scenario
    Write-Output "Verified successful cutover and Completed C# restart recovery."
}

function Invoke-CheckpointFailureRollbackScenario {
    param([Parameter(Mandatory = $true)]$Scenario)

    Write-Output "Running isolated checkpoint failure and Node rollback recovery..."
    $node = Start-InitialNodeHost $Scenario
    $nodeProcessId = [int]$node.processId
    Assert-OwnerLease $Scenario "node" "production" $nodeProcessId

    $desktopRun = Start-DesktopCommand `
        $Scenario `
        @("--bridge-cutover-to-dotnet", "--confirm-production-cutover")
    $checkpointPath = Join-Path `
        $Scenario.DataDirectory `
        "bridge-host-cutover.checkpoint.json"
    $null = Wait-CheckpointStage $Scenario "NodeStopRequested" 30
    $checkpointLock = $null
    try {
        $checkpointLock = [IO.File]::Open(
            $checkpointPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $cutover = Wait-CapturedProcess $desktopRun 120 "Fault-injected desktop cutover"
    } finally {
        if ($null -ne $checkpointLock) {
            $checkpointLock.Dispose()
        }
    }
    Assert-Condition `
        ($cutover.ExitCode -ne 0) `
        "Fault-injected cutover unexpectedly reported success."
    Assert-Condition `
        (-not (Test-ProcessAlive $nodeProcessId)) `
        "Fault-injected cutover did not stop the authenticated Node Host."
    Assert-Condition `
        (-not (Test-PublicEndpointAlive $Scenario)) `
        "Fault-injected cutover unexpectedly left an endpoint online."
    Assert-NoOwnerLease $Scenario
    $failedCheckpoint = Read-Checkpoint $Scenario
    Assert-Condition `
        ([string]$failedCheckpoint.stage -ceq "NodeStopRequested") `
        "Fault injection did not preserve the last durable NodeStopRequested stage."

    $recovery = Invoke-DesktopCommand $Scenario @("--bridge-service") 120
    if ($recovery.ExitCode -ne 0) {
        throw "Desktop rollback recovery failed. stdout=$($recovery.Output) stderr=$($recovery.Error)"
    }
    $rolledBackNode = Wait-AuthenticatedHost $Scenario "node" "production" 0 30
    $rolledBackProcessId = [int]$rolledBackNode.processId
    Assert-Condition `
        ($rolledBackProcessId -ne $nodeProcessId) `
        "Rollback recovery did not start a new Node Host."
    Assert-OwnerLease $Scenario "node" "production" $rolledBackProcessId
    $rolledBackCheckpoint = Read-Checkpoint $Scenario
    Assert-Condition `
        ([string]$rolledBackCheckpoint.stage -ceq "RolledBack") `
        "Rollback recovery did not converge the checkpoint to RolledBack."
    Assert-Condition `
        ([int]$rolledBackCheckpoint.nodeRollbackProcessId -eq $rolledBackProcessId) `
        "RolledBack checkpoint did not bind the recovered Node PID."

    Stop-AuthenticatedHost $Scenario "node" "production" $rolledBackProcessId
    Assert-NoOwnerLease $Scenario
    Write-Output "Verified checkpoint persistence failure and durable Node rollback recovery."
}

function Stop-ScenarioHostIfPresent {
    param([Parameter(Mandatory = $true)]$Scenario)

    $status = Get-AuthenticatedStatus $Scenario "health"
    if ($null -ne $status -and
        $status.ok -eq $true -and
        [int]$status.managementApiVersion -eq 1 -and
        [string]$status.ownershipMode -ceq "active" -and
        $status.activeOwner -eq $true -and
        [int]$status.processId -gt 0) {
        $hostKind = [string]$status.hostKind
        $instanceName = [string]$status.instanceName
        $supportedIdentity =
            ($hostKind -ceq "node" -and $instanceName -ceq "production") -or
            ($hostKind -ceq "dotnet" -and $instanceName -ceq "production-dotnet")
        if (-not $supportedIdentity) {
            throw "Cleanup found an authenticated but unsupported Host identity."
        }
        Stop-AuthenticatedHost `
            $Scenario `
            $hostKind `
            $instanceName `
            ([int]$status.processId)
    }

    foreach ($processId in $Scenario.KnownProcessIds) {
        if (Test-ProcessAlive $processId) {
            throw "An isolated Bridge Host is still alive after authenticated cleanup."
        }
    }
    if ($null -ne $Scenario.InitialNodeRun) {
        if ($Scenario.InitialNodeRun.Process.HasExited) {
            $Scenario.InitialNodeRun.Process.WaitForExit()
            $null = $Scenario.InitialNodeRun.Stdout.GetAwaiter().GetResult()
            $null = $Scenario.InitialNodeRun.Stderr.GetAwaiter().GetResult()
        }
        $Scenario.InitialNodeRun.Process.Dispose()
        $Scenario.InitialNodeRun = $null
    }
}

try {
    New-Item -ItemType Directory -Path $verificationRoot | Out-Null
    Write-Output "Building Node production assets..."
    Invoke-Checked "npm.cmd" @("run", "build") $projectDirectory
    Write-Output "Publishing desktop and C# sidecars..."
    Invoke-Checked "dotnet.exe" @(
        "publish",
        ".\desktop-control\AiCliFeishuControl.csproj",
        "-c",
        "Release",
        "-o",
        $publishDirectory
    ) $projectDirectory
    Write-Output "Desktop publish completed; preparing isolated Hosts..."

    foreach ($executableName in @(
        "AiCliFeishuControl.exe",
        "AiCliFeishuTerminalHost.exe",
        "AiCliFeishuBridgeHost.exe"
    )) {
        $executablePath = Join-Path $publishDirectory $executableName
        Assert-Condition `
            (Test-Path -LiteralPath $executablePath -PathType Leaf) `
            "Desktop publish is missing $executableName."
        $executable = Get-Item -LiteralPath $executablePath
        Assert-Condition `
            ($executable.VersionInfo.FileVersion -ceq $expectedFileVersion) `
            "$executableName version mismatch."
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
        Assert-Condition `
            (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $forbiddenName))) `
            "Desktop publish unexpectedly contains $forbiddenName."
    }

    $proxyPort = Get-FreeLoopbackPort
    $proxyRun = Start-RejectProxy $proxyPort
    $proxyUri = "http://127.0.0.1:$proxyPort"
    $successfulScenario = New-IsolatedScenario "success" $proxyUri
    Invoke-SuccessfulCutoverScenario $successfulScenario
    $rollbackScenario = New-IsolatedScenario "rollback" $proxyUri
    Invoke-CheckpointFailureRollbackScenario $rollbackScenario

    Write-Output "Verified desktop publish: $publishDirectory"
    Write-Output "Verified production cutover entry with fake credentials and loopback reject proxy."
    Write-Output "Verified no real Feishu or CLI command was required."
} catch {
    $verificationError = $_
} finally {
    $cleanupErrors = [Collections.Generic.List[string]]::new()
    foreach ($scenario in $scenarios) {
        try {
            Stop-ScenarioHostIfPresent $scenario
        } catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
        try {
            if (Test-Path -LiteralPath $scenario.NodeModulesLink) {
                Remove-Item -LiteralPath $scenario.NodeModulesLink -Force
            }
        } catch {
            $cleanupErrors.Add("Could not remove node_modules junction: $($_.Exception.Message)")
        }
    }
    if ($null -ne $proxyRun) {
        try {
            if (-not $proxyRun.Process.HasExited) {
                $proxyRun.Process.Kill($true)
                $null = $proxyRun.Process.WaitForExit(10000)
            }
            $null = $proxyRun.Stdout.GetAwaiter().GetResult()
            $null = $proxyRun.Stderr.GetAwaiter().GetResult()
            $proxyRun.Process.Dispose()
        } catch {
            $cleanupErrors.Add("Could not stop reject proxy: $($_.Exception.Message)")
        }
    }

    $resolvedVerificationRoot = [IO.Path]::GetFullPath($verificationRoot)
    $temporaryDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedVerificationRoot.StartsWith(
        $temporaryDirectory,
        [StringComparison]::OrdinalIgnoreCase)) {
        $cleanupErrors.Add("Verification output escaped the temporary directory.")
    } elseif ($cleanupErrors.Count -eq 0 -and
        (Test-Path -LiteralPath $resolvedVerificationRoot)) {
        try {
            Remove-Item -LiteralPath $resolvedVerificationRoot -Recurse -Force
        } catch {
            $cleanupErrors.Add("Could not remove verification directory: $($_.Exception.Message)")
        }
    }

    if ($null -ne $verificationError) {
        if ($cleanupErrors.Count -gt 0) {
            throw "$($verificationError.Exception.Message) Cleanup errors: $($cleanupErrors -join ' | ')"
        }
        throw $verificationError
    }
    if ($cleanupErrors.Count -gt 0) {
        throw "Production cutover verification cleanup failed: $($cleanupErrors -join ' | ')"
    }
}
