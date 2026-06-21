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

    $target = Resolve-TrackStashCatalogProjectPath
    if ($target -is [System.Management.Automation.ApplicationInfo]) {
        return & $target @Arguments
    }

    return & dotnet run --project $target -- @Arguments
}