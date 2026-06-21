function New-TrackStashCatalogYamlArtifacts {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline)]
        [object[]]$InputObject,

        [string]$RootPath = (Get-Location).Path
    )

    throw [System.NotImplementedException]::new('New-TrackStashCatalogYamlArtifacts is scaffolded but not yet implemented.')
}