function New-TrackStashArtistYamlArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$Name,

        [string]$Id,

        [string]$RootPath = (Get-Location).Path
    )

    return New-TrackStashEntityYamlArtifact -Kind artist -Name $Name -Id $Id -RootPath $RootPath
}