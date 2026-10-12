# Stops the stack. Add -Volumes to also wipe SQL Server data.
param([switch]$Volumes)
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $root
try {
    $profiles = @('--profile', 'apps', '--profile', 'observability')
    if ($Volumes) { docker compose @profiles down -v } else { docker compose @profiles down }
}
finally { Pop-Location }
