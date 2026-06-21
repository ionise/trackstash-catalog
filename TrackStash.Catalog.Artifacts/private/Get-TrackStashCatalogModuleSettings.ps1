<#
.SYNOPSIS
Loads module installation settings.

.DESCRIPTION
Reads optional module settings written by the TrackStash installer so cmdlets
can resolve catalog command path and default catalog context without requiring
manual per-session configuration.
#>
function Get-TrackStashCatalogModuleSettings {
    [CmdletBinding()]
    param()

    $moduleRoot = Split-Path -Parent $PSScriptRoot
    $settingsPath = Join-Path $moduleRoot 'TrackStash.Catalog.Artifacts.settings.json'
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        return $null
    }

    try {
        return (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json)
    }
    catch {
        return $null
    }
}