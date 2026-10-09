# Stops the stack. Add -Volumes to also wipe SQL Server data.
param([switch]$Volumes)
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $root
try {
    if ($Volumes) { docker compose --profile apps down -v } else { docker compose --profile apps down }
}
finally { Pop-Location }
