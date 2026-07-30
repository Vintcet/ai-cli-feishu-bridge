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
    if ($health.ok) {
        exit 0
    }
} catch {
    # The bridge is not running yet.
}

$runner = Join-Path $PSScriptRoot "run-bridge.ps1"
$pwshExecutable = (Get-Command pwsh.exe -ErrorAction Stop).Source
Start-Process -FilePath $pwshExecutable `
    -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", $runner, "-Port", $Port) `
    -WindowStyle Hidden
