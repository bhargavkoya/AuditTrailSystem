<#
  Starts the whole AuditFlow stack: SQL Server, Service Bus emulator, gateway and the 6 services (Docker).
  -Observability also starts the local OpenTelemetry dashboard (http://localhost:18888) and points every service at it.
  The React app runs separately: cd frontend/apps/web; npm run dev
#>
param([switch]$Observability)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $root
try {
    # docker writes progress to stderr; don't let that abort the script, rely on the exit code.
    $ErrorActionPreference = 'Continue'
    $profiles = @('--profile', 'apps')
    if ($Observability) {
        $profiles += @('--profile', 'observability')
        $env:OTEL_EXPORTER_OTLP_ENDPOINT = 'http://otel-dashboard:18889'   # read by compose, passed to every service
    }
    docker compose @profiles up -d --build 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw 'docker compose failed' }
    $ErrorActionPreference = 'Stop'

    Write-Host 'Waiting for gateway and services to report healthy...'
    $names = 'auth', 'engagement', 'workflow', 'attachment', 'review', 'notification'
    $deadline = (Get-Date).AddSeconds(120)
    do {
        Start-Sleep -Seconds 3
        $results = foreach ($n in $names) {
            try { (Invoke-RestMethod "http://localhost:5000/health/$n" -TimeoutSec 3).status } catch { 'Down' }
        }
        $ready = ($results | Where-Object { $_ -eq 'Healthy' }).Count
        Write-Host "  healthy: $ready / $($names.Count)"
    } while ($ready -lt $names.Count -and (Get-Date) -lt $deadline)

    if ($ready -lt $names.Count) { Write-Warning 'Not everything is healthy yet. Check: docker compose --profile apps ps / logs'; exit 1 }
    Write-Host 'Stack is up.  Gateway: http://localhost:5000   Swagger per service: http://localhost:500N/swagger'
    if ($Observability) { Write-Host 'Telemetry:     http://localhost:18888  (traces, structured logs, metrics)' }
    Write-Host 'Frontend:      cd frontend/apps/web ; npm install ; npm run dev   (http://localhost:5173)'
}
finally { Pop-Location }
