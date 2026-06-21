<#
.SYNOPSIS
Builds a Recording entity envelope as a PowerShell object.

.DESCRIPTION
Creates a schema-compatible recording object that can be piped to
ConvertTo-TrackStashCatalogYamlArtifact and Publish-TrackStashCatalogArtifact.

.PARAMETER Title
Recording title.

.PARAMETER Name
Optional display name. Defaults to Title.
#>
function New-TrackStashCatalogRecording {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Title,

        [string]$Name,

        [string]$Id,

        [string]$NormalizedName,

        [string]$MixName,

        [string]$Isrc,

        [ValidateSet('replace', 'merge', 'create-only', 'update-only')]
        [string]$Mode = 'replace',

        [hashtable[]]$ArtistCredit,

        [hashtable[]]$ReleaseLink,

        [hashtable[]]$Relationship,

        [Alias('ExternalId')]
        [hashtable[]]$ExternalRef,

        [AllowNull()]
        [object]$SourcePayload,

        [datetimeoffset]$CreatedUtc,

        [datetimeoffset]$UpdatedUtc
    )

    if ([string]::IsNullOrWhiteSpace($Name)) {
        $Name = $Title
    }

    $identity = $null
    try {
        $identity = Resolve-TrackStashCatalogEntityIdentity -Value $Title
    }
    catch {
    }

    if ([string]::IsNullOrWhiteSpace($NormalizedName)) {
        if ($null -ne $identity -and -not [string]::IsNullOrWhiteSpace([string]$identity.normalizedName)) {
            $NormalizedName = [string]$identity.normalizedName
        }
        else {
            $NormalizedName = (($Title -replace '[^\p{L}\p{Nd}]+', ' ').Trim().ToLowerInvariant() -replace '\s+', ' ')
        }
    }

    $slug = if ($null -ne $identity -and -not [string]::IsNullOrWhiteSpace([string]$identity.slug)) {
        [string]$identity.slug
    }
    else {
        (($Title -replace '[^\p{L}\p{Nd}]+', '-').Trim('-').ToLowerInvariant() -replace '-{2,}', '-')
    }

    if ([string]::IsNullOrWhiteSpace($Id)) {
        $Id = 'rec_' + $slug
    }

    $sourcePayloadJson = $null
    if ($SourcePayload -is [string]) {
        $sourcePayloadJson = [string]$SourcePayload
    }
    elseif ($null -ne $SourcePayload) {
        $sourcePayloadJson = ($SourcePayload | ConvertTo-Json -Depth 20)
    }

    $spec = [ordered]@{
        id = $Id
        title = $Title
        name = $Name
        normalizedName = $NormalizedName
    }

    if (-not [string]::IsNullOrWhiteSpace($MixName)) {
        $spec.mixName = $MixName
    }

    if (-not [string]::IsNullOrWhiteSpace($Isrc)) {
        $spec.isrc = $Isrc
    }

    if (-not [string]::IsNullOrWhiteSpace($sourcePayloadJson)) {
        $spec.sourcePayloadJson = $sourcePayloadJson
    }

    if ($PSBoundParameters.ContainsKey('CreatedUtc')) {
        $spec.createdUtc = $CreatedUtc.ToString('O')
    }

    if ($PSBoundParameters.ContainsKey('UpdatedUtc')) {
        $spec.updatedUtc = $UpdatedUtc.ToString('O')
    }

    if ($null -ne $ExternalRef -and $ExternalRef.Count -gt 0) {
        $spec.externalRefs = @($ExternalRef)
    }

    if ($null -ne $ArtistCredit -and $ArtistCredit.Count -gt 0) {
        $spec.artistCredits = @($ArtistCredit)
    }

    if ($null -ne $ReleaseLink -and $ReleaseLink.Count -gt 0) {
        $spec.releaseLinks = @($ReleaseLink)
    }

    if ($null -ne $Relationship -and $Relationship.Count -gt 0) {
        $spec.relationships = @($Relationship)
    }

    $entity = [pscustomobject]@{
        apiVersion = 'catalog.trackstash/v1'
        kind = 'Recording'
        mode = $Mode
        metadata = [ordered]@{ id = $Id }
        spec = $spec
    }

    $entity.PSTypeNames.Insert(0, 'TrackStash.Catalog.EntityEnvelope')
    return $entity
}