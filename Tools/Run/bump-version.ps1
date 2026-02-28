param(
    [string]$ConfigPath = "..\..\Properties\PublishConfiguration.xml"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "PublishConfiguration.xml introuvable: $ConfigPath"
}

[xml]$xml = Get-Content -LiteralPath $ConfigPath
$node = $xml.SelectSingleNode("/Publish/ModVersion")
if ($null -eq $node) {
    throw "Noeud /Publish/ModVersion introuvable."
}

$current = [string]$node.GetAttribute("Value")
if ([string]::IsNullOrWhiteSpace($current)) {
    throw "ModVersion vide."
}

$parts = $current.Split('.')
if ($parts.Count -lt 2) {
    throw "Format ModVersion invalide: '$current' (attendu: 1.39 ou 1.39.2)."
}

$lastIndex = $parts.Count - 1
[int]$last = 0
if (-not [int]::TryParse($parts[$lastIndex], [ref]$last)) {
    throw "Dernier segment non numerique dans ModVersion: '$current'"
}

$parts[$lastIndex] = ($last + 1).ToString()
$next = [string]::Join(".", $parts)
$node.SetAttribute("Value", $next)
$xml.Save((Resolve-Path -LiteralPath $ConfigPath))

Write-Host "ModVersion: $current -> $next"
