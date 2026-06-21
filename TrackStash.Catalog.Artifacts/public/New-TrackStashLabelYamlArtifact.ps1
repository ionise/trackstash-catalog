function New-TrackStashLabelYamlArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$Name,

        [string]$RootPath = (Get-Location).Path
    )

    throw [System.NotImplementedException]::new('New-TrackStashLabelYamlArtifact is scaffolded but not yet implemented.')
}