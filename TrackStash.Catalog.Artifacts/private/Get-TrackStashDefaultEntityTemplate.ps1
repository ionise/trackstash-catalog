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

    throw [System.NotImplementedException]::new('Get-TrackStashDefaultEntityTemplate is scaffolded but not yet implemented.')
}