<#
.SYNOPSIS
Builds a Label entity envelope as a PowerShell object.

.DESCRIPTION
Creates a schema-compatible catalog entity object that can be piped to
ConvertTo-TrackStashCatalogYamlArtifact and then Publish-TrackStashCatalogArtifact.

.PARAMETER Name
Display name for the label.

.PARAMETER Id
Optional explicit label ID. When omitted, a slug-based ID is generated.

.PARAMETER ExternalRef
Optional external reference mappings.

.PARAMETER Image
Optional image mappings retained in source payload JSON for forward compatibility.
#>
function New-TrackStashCatalogLabel {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [string]$Id,

        [string]$NormalizedName,

        [string]$SortName,

        [ValidateSet('replace', 'merge', 'create-only', 'update-only')]
        [string]$Mode = 'replace',

        [hashtable[]]$Alias,

        [Alias('ExternalId')]
        [hashtable[]]$ExternalRef,

        [AllowNull()]
        [object]$SourcePayload,

        [datetimeoffset]$LatestRelease,

        [hashtable[]]$Image,

        [datetimeoffset]$CreatedUtc,

        [datetimeoffset]$UpdatedUtc
    )

    $identity = $null
    try {
        $identity = Resolve-TrackStashCatalogEntityIdentity -Value $Name
    }
    catch {
    }

    if ([string]::IsNullOrWhiteSpace($NormalizedName)) {
        if ($null -ne $identity -and -not [string]::IsNullOrWhiteSpace([string]$identity.normalizedName)) {
            $NormalizedName = [string]$identity.normalizedName
        }
        else {
            $NormalizedName = (($Name -replace '[^\p{L}\p{Nd}]+', ' ').Trim().ToLowerInvariant() -replace '\s+', ' ')
        }
    }

    $slug = if ($null -ne $identity -and -not [string]::IsNullOrWhiteSpace([string]$identity.slug)) {
        [string]$identity.slug
    }
    else {
        (($Name -replace '[^\p{L}\p{Nd}]+', '-').Trim('-').ToLowerInvariant() -replace '-{2,}', '-')
    }

    if ([string]::IsNullOrWhiteSpace($Id)) {
        $Id = 'lbl_' + $slug
    }

    if ([string]::IsNullOrWhiteSpace($SortName)) {
        $SortName = $Name
    }

    $payloadMap = $null
    $sourcePayloadJson = $null
    if ($SourcePayload -is [string]) {
        $sourcePayloadJson = [string]$SourcePayload
    }
    elseif ($null -ne $SourcePayload -or $PSBoundParameters.ContainsKey('LatestRelease') -or ($null -ne $Image -and $Image.Count -gt 0)) {
        $payloadMap = [ordered]@{}

        if ($SourcePayload -is [System.Collections.IDictionary]) {
            foreach ($k in $SourcePayload.Keys) {
                $payloadMap[[string]$k] = $SourcePayload[$k]
            }
        }
        elseif ($null -ne $SourcePayload) {
            $payloadMap.value = $SourcePayload
        }

        if ($PSBoundParameters.ContainsKey('LatestRelease')) {
            $payloadMap.latestReleaseUtc = $LatestRelease.ToString('O')
        }

        if ($null -ne $Image -and $Image.Count -gt 0) {
            $payloadMap.images = @($Image)
        }

        if ($payloadMap.Count -gt 0) {
            $sourcePayloadJson = $payloadMap | ConvertTo-Json -Depth 20
        }
    }

    $spec = [ordered]@{
        id = $Id
        name = $Name
        normalizedName = $NormalizedName
        sortName = $SortName
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

    if ($null -ne $Alias -and $Alias.Count -gt 0) {
        $spec.aliases = @($Alias)
    }

    if ($null -ne $ExternalRef -and $ExternalRef.Count -gt 0) {
        $spec.externalRefs = @($ExternalRef)
    }

    $entity = [pscustomobject]@{
        apiVersion = 'catalog.trackstash/v1'
        kind = 'Label'
        mode = $Mode
        metadata = [ordered]@{ id = $Id }
        spec = $spec
    }

    $entity.PSTypeNames.Insert(0, 'TrackStash.Catalog.EntityEnvelope')
    return $entity
}