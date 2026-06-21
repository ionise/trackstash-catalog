<#
.SYNOPSIS
Creates a YAML artifact for a recording.

.DESCRIPTION
Generates a catalog-ready YAML document for a recording, using catalog-backed
slug and normalized-name resolution.

.PARAMETER Name
Recording display name.

.PARAMETER Id
Optional explicit entity ID.

.PARAMETER RootPath
Root folder where the artifact folder structure should be created.
#>
function New-TrackStashRecordingYamlArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$Name,

        [string]$Id,

        [string]$RootPath = (Get-Location).Path
    )

    return New-TrackStashEntityYamlArtifact -Kind recording -Name $Name -Id $Id -RootPath $RootPath
}