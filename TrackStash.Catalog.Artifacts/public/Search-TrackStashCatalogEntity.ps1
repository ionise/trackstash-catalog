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

    if ([string]::IsNullOrWhiteSpace($Query)) {
        throw 'Query cannot be empty.'
    }

    # Fast path for explicit entity IDs.
    if ($Query -match '^(lbl_|art_|rel_|rec_)') {
        return @(Get-TrackStashCatalogEntity -Id $Query)
    }

    return Find-TrackStashCatalogEntity -Name $Query
}