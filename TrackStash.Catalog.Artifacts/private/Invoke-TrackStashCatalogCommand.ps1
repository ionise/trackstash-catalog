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