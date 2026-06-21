<#
.SYNOPSIS
Converts a value to a catalog slug.

.DESCRIPTION
Resolves the catalog-backed entity identity for a value and returns its slug.
This keeps slug generation aligned with the catalog CLI and core normalization.

.PARAMETER Value
The source display value to slugify.
#>
function ConvertTo-TrackStashSlug {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    return (Resolve-TrackStashCatalogEntityIdentity -Value $Value).slug
}