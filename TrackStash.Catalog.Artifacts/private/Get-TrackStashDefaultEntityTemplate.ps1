function Get-TrackStashDefaultEntityTemplate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('label', 'artist', 'release', 'recording')]
        [string]$Kind
    )

    throw [System.NotImplementedException]::new('Get-TrackStashDefaultEntityTemplate is scaffolded but not yet implemented.')
}