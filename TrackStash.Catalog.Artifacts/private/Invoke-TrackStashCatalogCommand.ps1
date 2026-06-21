<#
.SYNOPSIS
Invokes the catalog CLI.

.DESCRIPTION
Runs a catalog command either through the installed trackstash-catalog command
or by falling back to dotnet run against the local project.

.PARAMETER Arguments
Arguments to pass to the catalog command.
#>
function Invoke-TrackStashCatalogCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $settings = Get-TrackStashCatalogModuleSettings
    if ($null -ne $settings) {
        if ([string]::IsNullOrWhiteSpace($env:TRACKSTASH_CATALOG) -and -not [string]::IsNullOrWhiteSpace($settings.DefaultCatalog)) {
            [Environment]::SetEnvironmentVariable('TRACKSTASH_CATALOG', [string]$settings.DefaultCatalog, 'Process')
        }

        if ([string]::IsNullOrWhiteSpace($env:TRACKSTASH_PROVIDER) -and -not [string]::IsNullOrWhiteSpace($settings.DefaultProvider)) {
            [Environment]::SetEnvironmentVariable('TRACKSTASH_PROVIDER', [string]$settings.DefaultProvider, 'Process')
        }

        if ([string]::IsNullOrWhiteSpace($env:TRACKSTASH_SQLITE_DB_PATH) -and -not [string]::IsNullOrWhiteSpace($settings.SqliteDbPath)) {
            [Environment]::SetEnvironmentVariable('TRACKSTASH_SQLITE_DB_PATH', [string]$settings.SqliteDbPath, 'Process')
        }
    }

    $target = Resolve-TrackStashCatalogProjectPath
    if ($target -is [System.Management.Automation.ApplicationInfo]) {
        return & $target @Arguments
    }

    if ($target -is [string] -and $target.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) {
        return & dotnet run --project $target -- @Arguments
    }

    return & $target @Arguments
}