function ConvertTo-TrackStashSlug {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    return (Resolve-TrackStashCatalogEntityIdentity -Value $Value).slug
}