function New-TrackStashReleaseYamlArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$Name,

        [string]$Id,

        [string]$RootPath = (Get-Location).Path
    )

    return New-TrackStashEntityYamlArtifact -Kind release -Name $Name -Id $Id -RootPath $RootPath
}