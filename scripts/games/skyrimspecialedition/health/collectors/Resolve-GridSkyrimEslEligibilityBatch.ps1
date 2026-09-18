#requires -Version 5.1

<#
.SYNOPSIS
Reduces a bounded set of verified xEdit ESL audit rows without inferring mod roles.
.DESCRIPTION
Produces one single-plugin eligibility artifact per explicit target plus one
case-local aggregate. It is read-only and grants no mutation authority.
#>
function Resolve-GridSkyrimEslEligibilityBatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Evidence,
        [Parameter(Mandatory)][string[]]$PluginNames,
        [Parameter(Mandatory)][string]$ContextFingerprint,
        [Parameter(Mandatory)][string]$CaseDirectory,
        [ValidateRange(1, 64)][int]$MaximumPlugins = 64
    )

    $plugins = @($PluginNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    if ($plugins.Count -eq 0) { throw 'EslEligibilityBatchEmpty: at least one exact plugin is required.' }
    if ($plugins.Count -gt $MaximumPlugins) { throw "EslEligibilityBatchLimitExceeded: $($plugins.Count) plugins exceed the $MaximumPlugins-plugin limit." }
    $fullCase = [IO.Path]::GetFullPath($CaseDirectory)
    if (-not (Test-Path -LiteralPath $fullCase -PathType Container)) { throw "EslEligibilityBatchCaseMissing: $fullCase" }
    $resultDirectory = Join-Path $fullCase 'esl-eligibility'
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null

    $items = New-Object Collections.Generic.List[object]
    for ($index = 0; $index -lt $plugins.Count; $index++) {
        $plugin = [string]$plugins[$index]
        $bytes = [Text.Encoding]::UTF8.GetBytes($plugin.ToUpperInvariant())
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $identity = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant().Substring(0, 16) }
        finally { $sha.Dispose() }
        $outputPath = Join-Path $resultDirectory ('{0:D3}-{1}.v1.json' -f ($index + 1), $identity)
        $resolved = Resolve-GridSkyrimEslEligibility -Evidence $Evidence -PluginName $plugin `
            -ContextFingerprint $ContextFingerprint -CaseDirectory $fullCase -OutputPath $outputPath
        $items.Add([pscustomobject][ordered]@{ path = $resolved.Path; result = $resolved.Result })
    }

    $counts = [ordered]@{}
    foreach ($status in @('EligibleWithoutCompaction', 'CompactionRequired', 'Ineligible', 'UnsafeEngineRisk', 'AlreadyLight')) {
        $counts[$status] = @($items | Where-Object { [string]$_.result.status -ceq $status }).Count
    }
    $document = [pscustomobject][ordered]@{
        schemaVersion = 1
        status = 'Collected'
        contextFingerprint = $ContextFingerprint
        pluginCount = $plugins.Count
        counts = [pscustomobject]$counts
        items = $items.ToArray()
        mutationAuthorized = $false
    }
    $path = Join-Path $fullCase 'xedit-esl-eligibility-batch.v1.json'
    $document | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $path -Encoding UTF8
    [pscustomobject][ordered]@{ Path = $path; Result = $document }
}
