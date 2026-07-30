[CmdletBinding()]
param(
    [ValidateRange(0, 65535)]
    [int]$Port = 0
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent $PSScriptRoot

if ($Port -le 0) {
    $envFile = Join-Path $projectDirectory ".env"
    if (Test-Path -LiteralPath $envFile) {
        foreach ($line in Get-Content -LiteralPath $envFile) {
            $trimmed = $line.Trim()
            if (-not $trimmed.StartsWith("BRIDGE_HTTP_PORT=", [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            $value = $trimmed.Substring($trimmed.IndexOf('=') + 1).Trim().Trim('"').Trim("'")
            $parsedPort = 0
            if ([int]::TryParse($value, [ref]$parsedPort) -and $parsedPort -gt 0 -and $parsedPort -le 65535) {
                $Port = $parsedPort
            }
            break
        }
    }
}
if ($Port -le 0) {
    $Port = 8765
}

try {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 2
    if (-not $health.ok -or
        -not $health.processId -or
        [int]$health.processId -le 0) {
        throw "Port $Port is not the Codex Feishu bridge."
    }
} catch {
    $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalAddress -eq "127.0.0.1" } |
        Select-Object -First 1
    if (-not $listener) {
        exit 0
    }
    throw "Refusing to stop process on port $Port because bridge identity could not be verified."
}

Stop-Process -Id ([int]$health.processId) -Force -ErrorAction Stop
