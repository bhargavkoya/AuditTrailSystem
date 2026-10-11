<#
  Black-box permission audit: drives throwaway engagements through every lifecycle status via the gateway and checks every
  role x status x operation against tools/permission-audit/matrix.json. Prints a grid; exit code 1 on any mismatch.
  Needs the stack (./infra/local/up.ps1) and Node 20+. Safe to repeat: each run creates its own engagements.

    ./infra/local/permission-audit.ps1 [-Base http://localhost:5000] [-Quiet]
#>
param(
    [string]$Base = 'http://localhost:5000',
    [switch]$Quiet
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')

if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node 20+ is required (https://nodejs.org).' }
$nodeVersion = (node --version).TrimStart('v')
if ([int]($nodeVersion.Split('.')[0]) -lt 20) { throw "Node 20+ is required, found v$nodeVersion." }

$arguments = @((Join-Path $root 'tools\permission-audit\audit.mjs'), '--base', $Base)
if ($Quiet) { $arguments += '--quiet' }
& node @arguments
exit $LASTEXITCODE
