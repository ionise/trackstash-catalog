function New-TrackStashCatalogYamlArtifacts {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline)]
        [object[]]$InputObject,

        [string]$RootPath = (Get-Location).Path
    )

    begin {
        $results = New-Object System.Collections.Generic.List[object]
    }

    process {
        foreach ($item in $InputObject) {
            if ($null -eq $item) {
                continue
            }

            $kind = $item.Kind
            $name = $item.Name
            $id = $item.Id

            if ([string]::IsNullOrWhiteSpace($kind) -or [string]::IsNullOrWhiteSpace($name)) {
                throw "Each input object must provide Kind and Name properties."
            }

            switch ($kind.ToLowerInvariant()) {
                'label' { $results.Add((New-TrackStashLabelYamlArtifact -Name $name -Id $id -RootPath $RootPath)) }
                'artist' { $results.Add((New-TrackStashArtistYamlArtifact -Name $name -Id $id -RootPath $RootPath)) }
                'release' { $results.Add((New-TrackStashReleaseYamlArtifact -Name $name -Id $id -RootPath $RootPath)) }
                'recording' { $results.Add((New-TrackStashRecordingYamlArtifact -Name $name -Id $id -RootPath $RootPath)) }
                default { throw "Unsupported Kind '$kind'." }
            }
        }
    }

    end {
        $results
    }
}