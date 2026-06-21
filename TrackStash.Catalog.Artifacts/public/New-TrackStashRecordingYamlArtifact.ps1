function New-TrackStashRecordingYamlArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$Name,

        [string]$RootPath = (Get-Location).Path
    )

    throw [System.NotImplementedException]::new('New-TrackStashRecordingYamlArtifact is scaffolded but not yet implemented.')
}