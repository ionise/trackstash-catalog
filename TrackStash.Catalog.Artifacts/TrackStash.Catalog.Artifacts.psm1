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
    'New-TrackStashCatalogLabel',
    'New-TrackStashCatalogArtist',
    'New-TrackStashCatalogRelease',
    'New-TrackStashCatalogRecording',
    'ConvertTo-TrackStashCatalogYamlArtifact',
    'Set-TrackStashCatalogEntity',
    'Remove-TrackStashCatalogEntity',
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