<#
  ~100-user load check against the running stack: dashboard/open/save/upload/review scenarios, a contention group that provokes
  409s, 100 open SSE streams and a save-to-event delivery probe. Prints p50/p95/p99 per operation and PASS/FAIL against the
  stated targets (exit code 1 when one is missed). Needs the stack (./infra/local/up.ps1) and Node 20+.
  Creates about 70 throwaway engagements per run. Numbers are from this machine: they catch regressions, they are not capacity planning.

    ./infra/local/load-test.ps1                       # 100 users, 30 s ramp, 120 s steady
    ./infra/local/load-test.ps1 -Smoke                # 20 users, 25 s (what CI runs)
    ./infra/local/load-test.ps1 -Users 50 -Steady 60
#>
param(
    [int]$Users = 0,
    [int]$Ramp = 0,
    [int]$Steady = 0,
    [double]$TargetsScale = 1,
    [switch]$Smoke,
    [switch]$NoDocker,
    [string]$Base = 'http://localhost:5000'
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')

if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node 20+ is required (https://nodejs.org).' }
$nodeVersion = (node --version).TrimStart('v')
if ([int]($nodeVersion.Split('.')[0]) -lt 20) { throw "Node 20+ is required, found v$nodeVersion." }

$arguments = @((Join-Path $root 'tools\load-test\load.mjs'), '--base', $Base, '--targets-scale', "$TargetsScale")
if ($Smoke) { $arguments += '--smoke' }
if ($NoDocker) { $arguments += '--no-docker' }
if ($Users -gt 0) { $arguments += @('--users', "$Users") }
if ($Ramp -gt 0) { $arguments += @('--ramp', "$Ramp") }
if ($Steady -gt 0) { $arguments += @('--steady', "$Steady") }
& node @arguments
exit $LASTEXITCODE
