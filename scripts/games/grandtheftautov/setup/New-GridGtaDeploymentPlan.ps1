#requires -Version 5.1
Set-StrictMode -Version Latest

function New-GridGtaDeploymentPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$GameRoot,
        [Parameter(Mandatory)][string[]]$ArtifactRoot,
        [string]$RecipePath = (Join-Path $PSScriptRoot 'recipes\forever-together.v1.json')
    )

    $root = [IO.Path]::GetFullPath($GameRoot).TrimEnd([char[]]@('\','/'))
    if (-not (Test-Path -LiteralPath (Join-Path $root 'GTA5.exe') -PathType Leaf)) {
        throw "GtaLegacyRootRequired: '$root' does not contain GTA5.exe."
    }
    $recipe = Get-Content -LiteralPath ([IO.Path]::GetFullPath($RecipePath)) -Raw | ConvertFrom-Json
    if ([int]$recipe.schemaVersion -ne 1 -or [string]$recipe.gameEdition -cne 'Legacy') { throw 'GtaRecipeUnsupported: only Legacy recipe schema v1 is supported.' }

    $sourceRoots = @($ArtifactRoot | ForEach-Object { [IO.Path]::GetFullPath($_) } | Sort-Object -Unique)
    foreach ($sourceRoot in $sourceRoots) {
        if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw "ArtifactRootNotFound: '$sourceRoot'." }
    }

    $operations = @()
    $missing = @()
    $ambiguous = @()
    foreach ($artifact in @($recipe.artifacts)) {
        $matches = @($sourceRoots | ForEach-Object {
            Get-ChildItem -LiteralPath $_ -Filter ([string]$artifact.fileName) -File -Recurse -ErrorAction Stop
        } | Sort-Object FullName)
        if ($matches.Count -eq 0) { $missing += [string]$artifact.artifactId; continue }
        $observed = @($matches | ForEach-Object {
            [pscustomobject][ordered]@{ path=$_.FullName; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant() }
        })
        $hashes = @($observed.sha256 | Sort-Object -Unique)
        if ($hashes.Count -ne 1) {
            $ambiguous += [pscustomobject][ordered]@{ artifactId=[string]$artifact.artifactId; candidates=$observed }
            continue
        }
        $destination = [IO.Path]::GetFullPath((Join-Path $root ([string]$artifact.destination)))
        if (-not ($destination.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
            throw "RecipeTargetEscapesGameRoot: '$destination'."
        }
        $existingHash = if (Test-Path -LiteralPath $destination -PathType Leaf) { (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToUpperInvariant() } else { $null }
        $operations += [pscustomobject][ordered]@{
            kind='CopyFile'; artifactId=[string]$artifact.artifactId; sourcePath=[string]$observed[0].path
            sourceSha256=[string]$observed[0].sha256; destinationPath=$destination; priorSha256=$existingHash
            disposition=if($existingHash -ceq [string]$observed[0].sha256){'AlreadySatisfied'}elseif($existingHash){'Replace'}else{'Create'}
        }
    }
    foreach ($configuration in @($recipe.configuration)) {
        $path = [IO.Path]::GetFullPath((Join-Path $root ([string]$configuration.relativePath)))
        $operations += [pscustomobject][ordered]@{
            kind='SetIniValue'; configurationId=[string]$configuration.configurationId; destinationPath=$path
            key=[string]$configuration.key; value=[string]$configuration.value
            priorSha256=if(Test-Path -LiteralPath $path -PathType Leaf){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()}else{$null}
            disposition='EnsureValue'
        }
    }

    $status = if ($ambiguous.Count) { 'AmbiguousArtifacts' } elseif ($missing.Count) { 'NeedsArtifacts' } else { 'ReadyForReview' }
    $normalized = [pscustomobject][ordered]@{
        schemaVersion=1; recipeId=[string]$recipe.recipeId; recipeVersion=[string]$recipe.recipeVersion
        gameRoot=$root; artifactRoots=$sourceRoots; operations=@($operations); missingArtifactIds=@($missing); ambiguousArtifacts=@($ambiguous)
    }
    $sha = Get-GridCanonicalJsonSha256 -InputObject $normalized
    [pscustomobject][ordered]@{
        schemaVersion=1; specificationId=('gta-deployment-' + $sha.Substring(0,24).ToLowerInvariant())
        specificationSha256=$sha; status=$status; recipeId=[string]$recipe.recipeId; recipeVersion=[string]$recipe.recipeVersion
        gameRoot=$root; artifactRoots=$sourceRoots; operations=@($operations); missingArtifactIds=@($missing)
        ambiguousArtifacts=@($ambiguous); targets=@($operations | ForEach-Object { [string]$_.destinationPath } | Sort-Object -Unique)
        authorizationRequired=$true; changedExternalState=$false
    }
}
