$moduleRoot = $PSScriptRoot

foreach ($folder in @('classes', 'private', 'public')) {
    $folderPath = Join-Path $moduleRoot $folder
    if (-not (Test-Path -LiteralPath $folderPath)) {
        continue
    }

    Get-ChildItem -LiteralPath $folderPath -Filter '*.ps1' -File |
        Sort-Object Name |
        ForEach-Object {
            . $_.FullName
        }
}

Export-ModuleMember -Function @(
    'New-TrackStashLabelYamlArtifact',
    'New-TrackStashArtistYamlArtifact',
    'New-TrackStashReleaseYamlArtifact',
    'New-TrackStashRecordingYamlArtifact',
    'New-TrackStashCatalogYamlArtifacts',
    'Publish-TrackStashCatalogArtifact',
    'Get-TrackStashCatalogEntity',
    'Find-TrackStashCatalogEntity',
    'Search-TrackStashCatalogEntity',
    'Get-TrackStashCatalogSummary'
)