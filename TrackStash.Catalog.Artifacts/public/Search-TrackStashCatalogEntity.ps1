<#
.SYNOPSIS
Searches catalog entities.

.DESCRIPTION
This is the broad search entry point for catalog discovery. It is intended to
remain read-only and composable for scripting.

.PARAMETER Query
The search text or filter expression.
#>
function Search-TrackStashCatalogEntity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Query
    )

    throw [System.NotImplementedException]::new('Search-TrackStashCatalogEntity is scaffolded but not yet implemented.')
}