function Resolve-TrackStashArtifactPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('label', 'artist', 'release', 'recording')]
        [string]$Kind,

        [Parameter(Mandatory)]
        [string]$Slug,

        [string]$RootPath = (Get-Location).Path
    )

    throw [System.NotImplementedException]::new('Resolve-TrackStashArtifactPath is scaffolded but not yet implemented.')
}