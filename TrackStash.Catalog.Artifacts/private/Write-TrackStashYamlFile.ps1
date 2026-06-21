function Write-TrackStashYamlFile {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Content
    )

    throw [System.NotImplementedException]::new('Write-TrackStashYamlFile is scaffolded but not yet implemented.')
}