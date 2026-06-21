function New-TrackStashArtistYamlArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$Name,

        [string]$RootPath = (Get-Location).Path
    )

    throw [System.NotImplementedException]::new('New-TrackStashArtistYamlArtifact is scaffolded but not yet implemented.')
}