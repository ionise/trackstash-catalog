<#
.SYNOPSIS
Finds catalog entities using optional filters.

.DESCRIPTION
This is the future read/search entry point for catalog discovery. It is kept
as a public cmdlet stub so maintenance documentation can live alongside the
implementation.

.PARAMETER Kind
Optional entity kind filter.

.PARAMETER EntityType
Alias for Kind.

.PARAMETER Name
Optional exact or partial name filter.

.PARAMETER NormalizedName
Optional normalized name filter.

.PARAMETER Slug
Optional slug filter.

.PARAMETER Reference
Optional reference filter.
#>
function Find-TrackStashCatalogEntity {
    [CmdletBinding()]
    param(
        [Alias('EntityType')]
        [ValidateSet('label', 'artist', 'release', 'recording')]
        [string]$Kind,
        [string]$Name,
        [string]$NormalizedName,
        [string]$Slug,
        [string]$Reference
    )

    if ([string]::IsNullOrWhiteSpace($Name) -and
        [string]::IsNullOrWhiteSpace($NormalizedName) -and
        [string]::IsNullOrWhiteSpace($Slug) -and
        [string]::IsNullOrWhiteSpace($Reference)) {
        throw 'At least one filter is required: -Name, -NormalizedName, -Slug, or -Reference.'
    }

    if (-not [string]::IsNullOrWhiteSpace($Reference)) {
        throw 'Reference-based lookup is not available yet. Use -Name, -Slug, or -NormalizedName.'
    }

    $candidateSlug = $Slug
    $candidateNormalizedName = $NormalizedName

    if ([string]::IsNullOrWhiteSpace($candidateSlug) -or [string]::IsNullOrWhiteSpace($candidateNormalizedName)) {
        $identitySource = if (-not [string]::IsNullOrWhiteSpace($Name)) { $Name } else { $candidateSlug }
        if (-not [string]::IsNullOrWhiteSpace($identitySource)) {
            $identity = Resolve-TrackStashCatalogEntityIdentity -Value $identitySource
            if ([string]::IsNullOrWhiteSpace($candidateSlug)) {
                $candidateSlug = [string]$identity.slug
            }

            if ([string]::IsNullOrWhiteSpace($candidateNormalizedName)) {
                $candidateNormalizedName = [string]$identity.normalizedName
            }
        }
    }

    $kinds = if ([string]::IsNullOrWhiteSpace($Kind)) {
        @('label', 'artist', 'release', 'recording')
    }
    else {
        @($Kind)
    }

    $prefixByKind = @{
        label = 'lbl_'
        artist = 'art_'
        release = 'rel_'
        recording = 'rec_'
    }

    $results = New-Object System.Collections.Generic.List[object]
    foreach ($k in $kinds) {
        $prefix = [string]$prefixByKind[$k]
        $candidateIds = New-Object System.Collections.Generic.List[string]

        if (-not [string]::IsNullOrWhiteSpace($candidateSlug)) {
            $candidateIds.Add("$prefix$candidateSlug")
        }

        if (-not [string]::IsNullOrWhiteSpace($candidateNormalizedName)) {
            $candidateIds.Add("$prefix$candidateNormalizedName")
        }

        foreach ($candidateId in ($candidateIds | Select-Object -Unique)) {
            try {
                $entity = Get-TrackStashCatalogEntity -Id $candidateId -Kind $k
                if ($null -ne $entity) {
                    $results.Add($entity)
                    break
                }
            }
            catch {
                continue
            }
        }
    }

    return $results
}