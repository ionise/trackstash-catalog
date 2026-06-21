<#
.SYNOPSIS
Converts an object into a TrackStash YAML document.

.DESCRIPTION
This private helper is reserved for future YAML serialization support and is
documented now so its maintenance purpose is explicit.

.PARAMETER InputObject
The object to serialize.
#>
function ConvertTo-TrackStashYamlDocument {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$InputObject
    )

    function ConvertTo-TrackStashYamlLines {
        param(
            [AllowNull()]
            [object]$Node,

            [Parameter(Mandatory)]
            [int]$Indent,

            [string]$Key
        )

        $prefix = ' ' * $Indent
        $lines = New-Object System.Collections.Generic.List[string]

        if ($null -eq $Node) {
            if ([string]::IsNullOrWhiteSpace($Key)) {
                $null = $lines.Add($prefix + 'null')
            }
            else {
                $null = $lines.Add($prefix + $Key + ': null')
            }

            return @($lines)
        }

        if ($Node -is [System.Collections.IDictionary]) {
            if (-not [string]::IsNullOrWhiteSpace($Key)) {
                $null = $lines.Add($prefix + $Key + ':')
                $Indent += 2
                $prefix = ' ' * $Indent
            }

            foreach ($k in $Node.Keys) {
                $child = ConvertTo-TrackStashYamlLines -Node $Node[$k] -Indent $Indent -Key ([string]$k)
                foreach ($line in $child) {
                    $null = $lines.Add($line)
                }
            }

            return @($lines)
        }

        if ($Node -is [pscustomobject]) {
            $map = [ordered]@{}
            foreach ($prop in $Node.PSObject.Properties) {
                $map[$prop.Name] = $prop.Value
            }

            return ConvertTo-TrackStashYamlLines -Node $map -Indent $Indent -Key $Key
        }

        if ($Node -is [System.Collections.IList] -and -not ($Node -is [string])) {
            if (-not [string]::IsNullOrWhiteSpace($Key)) {
                $null = $lines.Add($prefix + $Key + ':')
                $Indent += 2
                $prefix = ' ' * $Indent
            }

            foreach ($item in $Node) {
                if ($item -is [System.Collections.IDictionary] -or $item -is [pscustomobject] -or ($item -is [System.Collections.IList] -and -not ($item -is [string]))) {
                    $null = $lines.Add($prefix + '-')
                    $child = ConvertTo-TrackStashYamlLines -Node $item -Indent ($Indent + 2) -Key $null
                    foreach ($line in $child) {
                        $null = $lines.Add($line)
                    }
                }
                else {
                    $scalarValue = if ($item -is [bool]) {
                        $item.ToString().ToLowerInvariant()
                    }
                    elseif ($item -is [datetime] -or $item -is [datetimeoffset]) {
                        Format-TrackStashYamlScalar ($item.ToString('O'))
                    }
                    else {
                        Format-TrackStashYamlScalar ([string]$item)
                    }

                    $null = $lines.Add($prefix + '- ' + $scalarValue)
                }
            }

            if ($Node.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($Key)) {
                $lines.Clear()
                $null = $lines.Add(($prefix.Substring(0, [Math]::Max(0, $prefix.Length - 2))) + $Key + ': []')
            }

            return @($lines)
        }

        $scalar = if ($Node -is [bool]) {
            $Node.ToString().ToLowerInvariant()
        }
        elseif ($Node -is [datetime] -or $Node -is [datetimeoffset]) {
            Format-TrackStashYamlScalar ($Node.ToString('O'))
        }
        elseif ($Node -is [int] -or $Node -is [long] -or $Node -is [double] -or $Node -is [decimal]) {
            [string]$Node
        }
        else {
            Format-TrackStashYamlScalar ([string]$Node)
        }

        if ([string]::IsNullOrWhiteSpace($Key)) {
            $null = $lines.Add($prefix + $scalar)
        }
        else {
            $null = $lines.Add($prefix + $Key + ': ' + $scalar)
        }

        return @($lines)
    }

    $lines = ConvertTo-TrackStashYamlLines -Node $InputObject -Indent 0 -Key $null
    return ($lines -join [Environment]::NewLine)
}