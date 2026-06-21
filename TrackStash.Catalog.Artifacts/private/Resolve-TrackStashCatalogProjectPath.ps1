<#
.SYNOPSIS
Resolves the catalog command source.

.DESCRIPTION
Returns either the installed trackstash-catalog command or the local project
path so the PowerShell module can invoke catalog consistently in development
and installed environments.
#>
function Resolve-TrackStashCatalogProjectPath {
    [CmdletBinding()]
    param()

    $explicitCommandPath = $env:TRACKSTASH_CATALOG_COMMAND_PATH
    if (-not [string]::IsNullOrWhiteSpace($explicitCommandPath)) {
        if (Test-Path -LiteralPath $explicitCommandPath) {
            return (Resolve-Path -LiteralPath $explicitCommandPath).Path
        }

        throw "TRACKSTASH_CATALOG_COMMAND_PATH is set but file was not found: $explicitCommandPath"
    }

    $settings = Get-TrackStashCatalogModuleSettings
    if ($null -ne $settings -and -not [string]::IsNullOrWhiteSpace($settings.CatalogCommandPath)) {
        if (Test-Path -LiteralPath $settings.CatalogCommandPath) {
            return (Resolve-Path -LiteralPath $settings.CatalogCommandPath).Path
        }
    }

    $command = Get-Command -Name 'trackstash-catalog' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command
    }

    $moduleRoot = Split-Path -Parent $PSScriptRoot
    $repoRoot = Split-Path -Parent $moduleRoot
    $projectPath = Join-Path $repoRoot 'src/TrackStash.Catalog/TrackStash.Catalog.csproj'

    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "Unable to locate trackstash-catalog. Set TRACKSTASH_CATALOG_COMMAND_PATH, install a trackstash-catalog command on PATH, or use source checkout with project at $projectPath."
    }

    return $projectPath
}