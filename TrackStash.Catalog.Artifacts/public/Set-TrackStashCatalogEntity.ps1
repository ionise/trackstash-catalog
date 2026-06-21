<#
.SYNOPSIS
Applies an entity envelope object to the catalog.

.DESCRIPTION
Takes an entity object, enforces a mode, writes/updates the YAML artifact, and
publishes it through the catalog apply pipeline.

.PARAMETER InputObject
Entity envelope object.

.PARAMETER Mode
Apply mode for the entity object before publishing.

.PARAMETER RootPath
Root folder for generated/updated YAML artifacts.
#>
function Set-TrackStashCatalogEntity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipeline)]
        [object]$InputObject,

        [ValidateSet('replace', 'merge', 'create-only', 'update-only')]
        [string]$Mode = 'merge',

        [string]$RootPath = (Get-Location).Path,

        [string]$Catalog,

        [string]$DbPath,

        [ValidateRange(0, 20)]
        [int]$RetryCount = 5,

        [ValidateRange(0, 30)]
        [int]$RetryDelaySeconds = 1
    )

    process {
        if ($null -eq $InputObject) {
            return
        }

        $working = $InputObject | ConvertTo-Json -Depth 100 | ConvertFrom-Json -Depth 100
        $working | Add-Member -NotePropertyName mode -NotePropertyValue $Mode -Force

        $artifact = $working | ConvertTo-TrackStashCatalogYamlArtifact -RootPath $RootPath

        $publishParams = @{
            Path = $artifact.Path
            RetryCount = $RetryCount
            RetryDelaySeconds = $RetryDelaySeconds
            PassThru = $true
        }

        if (-not [string]::IsNullOrWhiteSpace($Catalog)) {
            $publishParams.Catalog = $Catalog
        }

        if (-not [string]::IsNullOrWhiteSpace($DbPath)) {
            $publishParams.DbPath = $DbPath
        }

        Publish-TrackStashCatalogArtifact @publishParams
    }
}