<#
.SYNOPSIS
Gets one catalog entity by ID.

.DESCRIPTION
This is the single-entity read path for catalog discovery and inspection. The
cmdlet is intentionally separate from search so direct lookups stay explicit.

.PARAMETER Id
The catalog entity ID to retrieve.
#>
function Get-TrackStashCatalogEntity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Id
    )

    throw [System.NotImplementedException]::new('Get-TrackStashCatalogEntity is scaffolded but not yet implemented.')
}