<#
.SYNOPSIS
Gets the default template for an entity kind.

.DESCRIPTION
Returns the template scaffold that should be used when generating a new YAML
artifact for the specified catalog entity kind.

.PARAMETER Kind
Entity kind name.
#>
function Get-TrackStashDefaultEntityTemplate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('label', 'artist', 'release', 'recording')]
        [string]$Kind
    )

    $kindLower = $Kind.ToLowerInvariant()
    $kindTitle = $kindLower.Substring(0, 1).ToUpperInvariant() + $kindLower.Substring(1)

    $spec = [ordered]@{
        id = '<entity-id>'
        normalizedName = '<normalized-name>'
    }

    switch ($kindLower) {
        'label' {
            $spec.name = '<label-name>'
            $spec.sortName = '<label-sort-name>'
            break
        }
        'artist' {
            $spec.name = '<artist-name>'
            $spec.sortName = '<artist-sort-name>'
            break
        }
        'release' {
            $spec.title = '<release-title>'
            $spec.name = '<release-display-name>'
            $spec.artistCredits = @()
            $spec.labelLinks = @()
            $spec.externalRefs = @()
            break
        }
        'recording' {
            $spec.title = '<recording-title>'
            $spec.name = '<recording-display-name>'
            $spec.mixName = '<mix-name>'
            $spec.isrc = '<isrc>'
            $spec.artistCredits = @()
            $spec.releaseLinks = @()
            $spec.relationships = @()
            $spec.externalRefs = @()
            break
        }
    }

    return [ordered]@{
        apiVersion = 'catalog.trackstash/v1'
        kind = $kindTitle
        mode = 'replace'
        metadata = [ordered]@{
            id = '<entity-id>'
        }
        spec = $spec
    }
}