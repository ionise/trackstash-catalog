<#
.SYNOPSIS
Merges template data into a YAML template.

.DESCRIPTION
This helper is reserved for future templating support and documents the
maintenance boundary for template merging logic.

.PARAMETER Template
The template data structure.

.PARAMETER Data
The data to merge into the template.
#>
function Merge-TrackStashTemplateData {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Template,

        [Parameter(Mandatory)]
        [hashtable]$Data
    )

    function Copy-TrackStashNode {
        param(
            [Parameter(Mandatory)]
            [object]$Node
        )

        if ($Node -is [System.Collections.IDictionary]) {
            $copy = [ordered]@{}
            foreach ($key in $Node.Keys) {
                $copy[$key] = Copy-TrackStashNode -Node $Node[$key]
            }

            return $copy
        }

        if ($Node -is [System.Collections.IList] -and -not ($Node -is [string])) {
            $copy = New-Object System.Collections.Generic.List[object]
            foreach ($item in $Node) {
                $null = $copy.Add((Copy-TrackStashNode -Node $item))
            }

            return @($copy)
        }

        return $Node
    }

    function Merge-TrackStashNode {
        param(
            [Parameter(Mandatory)]
            [System.Collections.IDictionary]$Base,

            [Parameter(Mandatory)]
            [System.Collections.IDictionary]$Overlay
        )

        foreach ($key in $Overlay.Keys) {
            $overlayValue = $Overlay[$key]

            if (-not $Base.Contains($key)) {
                $Base[$key] = Copy-TrackStashNode -Node $overlayValue
                continue
            }

            $baseValue = $Base[$key]
            if ($baseValue -is [System.Collections.IDictionary] -and $overlayValue -is [System.Collections.IDictionary]) {
                Merge-TrackStashNode -Base $baseValue -Overlay $overlayValue
                continue
            }

            $Base[$key] = Copy-TrackStashNode -Node $overlayValue
        }
    }

    $merged = Copy-TrackStashNode -Node $Template
    Merge-TrackStashNode -Base $merged -Overlay $Data
    return $merged
}