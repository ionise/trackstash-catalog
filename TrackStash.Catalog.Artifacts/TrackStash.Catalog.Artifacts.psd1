@{
    RootModule = 'TrackStash.Catalog.Artifacts.psm1'
    ModuleVersion = '0.1.6'
    GUID = 'ca6c2b35-a350-4c46-843b-674dd217ab53'
    Author = 'TrackStash'
    CompanyName = 'TrackStash'
    Copyright = '(c) TrackStash. All rights reserved.'
    Description = 'TrackStash catalog artifact authoring and discovery module scaffold.'
    PowerShellVersion = '7.0'
    CompatiblePSEditions = @('Core')
    FunctionsToExport = @(
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
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
}
