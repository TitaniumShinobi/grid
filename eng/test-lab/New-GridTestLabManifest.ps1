[CmdletBinding(DefaultParameterSetName = 'Combined')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9][a-z0-9._-]{2,127}$')]
    [string]$LabId,

    [Parameter(Mandatory)]
    [ValidateSet('planned', 'downloaded', 'installed', 'native-mo2-verified', 'blocked', 'retired')]
    [string]$State,

    [Parameter(Mandatory, ParameterSetName = 'Combined')]
    [string]$ObservationPath,

    [Parameter(Mandatory, ParameterSetName = 'PrerequisiteReport')]
    [string]$BaseObservationPath,

    [Parameter(Mandatory, ParameterSetName = 'PrerequisiteReport')]
    [string]$PrerequisiteReportPath,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [string]$CreatedAtUtc = [DateTimeOffset]::UtcNow.UtcDateTime.ToString('O', [Globalization.CultureInfo]::InvariantCulture),
    [string]$UpdatedAtUtc
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:AllowedStates = @('planned', 'downloaded', 'installed', 'native-mo2-verified', 'blocked', 'retired')
$script:LabRoot = 'C:\Grid-Test-Lab'
$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:RepositoryEvidenceRoot = [System.IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot 'artifacts\test-lab'))

function Get-Properties {
    param([Parameter(Mandatory)] $Value)

    return @($Value.PSObject.Properties | Where-Object MemberType -in @('NoteProperty', 'Property'))
}

function Assert-ObjectShape {
    param(
        [Parameter(Mandatory)] $Value,
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string[]]$Allowed,
        [string[]]$Required = @()
    )

    if ($null -eq $Value -or ($Value -isnot [pscustomobject] -and $Value -isnot [System.Collections.IDictionary])) {
        throw "$Name must be a JSON object."
    }
    $properties = @(Get-Properties -Value $Value)
    foreach ($property in $properties) {
        if ($property.Name -notin $Allowed) { throw "$Name contains unsupported property '$($property.Name)'." }
    }
    foreach ($propertyName in $Required) {
        if ($null -eq $Value.PSObject.Properties[$propertyName]) { throw "$Name is missing required property '$propertyName'." }
    }
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory)] $Value,
        [Parameter(Mandatory)] [string]$Property,
        [Parameter(Mandatory)] [string]$Name
    )

    $member = $Value.PSObject.Properties[$Property]
    if ($null -eq $member) { throw "$Name is missing required property '$Property'." }
    if ($member.Value -is [Array]) { return ,$member.Value }
    return $member.Value
}

function Get-OptionalProperty {
    param(
        [Parameter(Mandatory)] $Value,
        [Parameter(Mandatory)] [string]$Property
    )

    $member = $Value.PSObject.Properties[$Property]
    if ($null -eq $member) { return $null }
    if ($member.Value -is [Array]) { return ,$member.Value }
    return $member.Value
}

function Assert-String {
    param(
        $Value,
        [Parameter(Mandatory)] [string]$Name,
        [int]$MinimumLength = 0,
        [int]$MaximumLength = 2048,
        [switch]$AllowNull
    )

    if ($null -eq $Value) {
        if ($AllowNull) { return }
        throw "$Name must be a string."
    }
    if ($Value -isnot [string]) { throw "$Name must be a string." }
    if ($Value.Length -lt $MinimumLength -or $Value.Length -gt $MaximumLength) {
        throw "$Name length must be between $MinimumLength and $MaximumLength characters."
    }
    if ($Value -match '[\u0000-\u001F\u007F]') { throw "$Name contains a control character." }
    if ($Value -match '(?i)-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' -or
        $Value -match '(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}' -or
        $Value -match '(?i)\b(?:password|passwd|token|access[_ -]?token|api[_ -]?key|client[_ -]?secret|authorization|cookie)\s*[:=]\s*\S+' -or
        $Value -match '\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b') {
        throw "$Name appears to contain credential material."
    }
}

function Assert-Boolean {
    param($Value, [Parameter(Mandatory)] [string]$Name)
    if ($Value -isnot [bool]) { throw "$Name must be a JSON boolean." }
}

function Assert-NonNegativeInteger {
    param($Value, [Parameter(Mandatory)] [string]$Name)

    $integerTypes = @([byte], [sbyte], [int16], [uint16], [int32], [uint32], [int64], [uint64])
    if ($null -eq $Value -or $Value.GetType() -notin $integerTypes -or [decimal]$Value -lt 0 -or [decimal]$Value -gt [long]::MaxValue) {
        throw "$Name must be a non-negative integer no larger than Int64.MaxValue."
    }
}

function Assert-Array {
    param(
        $Value,
        [Parameter(Mandatory)] [string]$Name,
        [int]$MinimumCount = 0,
        [Parameter(Mandatory)] [int]$MaximumCount
    )

    if ($null -eq $Value -or $Value -is [string] -or $Value -isnot [System.Collections.IEnumerable] -or $Value -is [pscustomobject]) {
        throw "$Name must be a JSON array."
    }
    $values = @($Value)
    if ($values.Count -lt $MinimumCount -or $values.Count -gt $MaximumCount) {
        throw "$Name must contain between $MinimumCount and $MaximumCount items."
    }
    return ,$values
}

function Assert-UtcTime {
    param($Value, [Parameter(Mandatory)] [string]$Name)

    Assert-String -Value $Value -Name $Name -MinimumLength 1 -MaximumLength 64
    if (-not $Value.EndsWith('Z', [StringComparison]::Ordinal)) { throw "$Name must be an ISO-8601 UTC timestamp ending in Z." }
    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($Value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed)) {
        throw "$Name must be a valid ISO-8601 UTC timestamp."
    }
}

function Assert-HttpsUrl {
    param($Value, [Parameter(Mandatory)] [string]$Name)

    Assert-String -Value $Value -Name $Name -MinimumLength 1 -MaximumLength 4096
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -cne 'https' -or [string]::IsNullOrWhiteSpace($uri.DnsSafeHost)) {
        throw "$Name must be an absolute HTTPS URL with a host."
    }
    if (-not [string]::IsNullOrEmpty($uri.UserInfo)) { throw "$Name must not contain URL user information." }
    if ($uri.Query -match '(?i)(?:token|key|secret|password|signature|credential|auth)=') {
        throw "$Name contains a credential-like query parameter."
    }
}

function Assert-Sha256 {
    param($Value, [Parameter(Mandatory)] [string]$Name)
    Assert-String -Value $Value -Name $Name -MinimumLength 64 -MaximumLength 64
    if ($Value -cnotmatch '^[A-Fa-f0-9]{64}$') { throw "$Name must be a 64-character hexadecimal SHA-256 value." }
}

function ConvertTo-CanonicalWindowsPath {
    param($Value, [Parameter(Mandatory)] [string]$Name)

    Assert-String -Value $Value -Name $Name -MinimumLength 3 -MaximumLength 32767
    if ($Value -notmatch '^[A-Za-z]:\\' -or $Value.Contains('/')) { throw "$Name must be an absolute Windows path using backslashes." }
    if ($Value -match '(?:^|\\)\.\.?($|\\)') { throw "$Name must not contain dot path segments." }
    $fullPath = [IO.Path]::GetFullPath($Value)
    $canonical = if ($fullPath.Length -eq 3) { $fullPath } else { $fullPath.TrimEnd('\') }
    $supplied = if ($Value.Length -eq 3) { $Value } else { $Value.TrimEnd('\') }
    if (-not [string]::Equals($canonical, $supplied, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name must already be a canonical absolute path."
    }
    return $canonical
}

function Test-IsSameOrDescendant {
    param([string]$Path, [string]$Root)
    return [string]::Equals($Path, $Root, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith(($Root.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoSecretContent {
    param($Value, [string]$Name = 'observations')

    if ($null -eq $Value) { return }
    if ($Value -is [string]) { Assert-String -Value $Value -Name $Name -MaximumLength 32767; return }
    if ($Value -is [ValueType]) { return }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [pscustomobject]) {
        $index = 0
        foreach ($item in $Value) { Assert-NoSecretContent -Value $item -Name "$Name[$index]"; $index++ }
        return
    }
    foreach ($property in @(Get-Properties -Value $Value)) {
        if ($property.Name -match '(?i)(?:password|passwd|token|api.?key|secret|authorization|cookie|credential)') {
            throw "$Name contains forbidden credential-like property '$($property.Name)'."
        }
        Assert-NoSecretContent -Value $property.Value -Name "$Name.$($property.Name)"
    }
}

function Read-ObservationJson {
    param([Parameter(Mandatory)] [string]$Path)

    if ($Path -match '^[dD]:[\\/]') { throw 'ObservationPath must never refer to drive D:.' }
    $fullPath = [IO.Path]::GetFullPath($Path)
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if ($item.PSIsContainer) { throw "ObservationPath is not a file: '$fullPath'." }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ObservationPath must not be a reparse point.' }
    if ($item.Length -gt 16MB) { throw 'Observation JSON exceeds the 16 MiB input limit.' }
    try {
        $json = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
        $convertParameters = @{ InputObject = $json; Depth = 32 }
        if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $convertParameters.DateKind = 'String' }
        $result = ConvertFrom-Json @convertParameters
    }
    catch { throw "Observation JSON could not be parsed: $($_.Exception.Message)" }
    if ($null -eq $result) { throw 'Observation JSON must contain an object.' }
    Assert-NoSecretContent -Value $result
    return [pscustomobject]@{ Path = $fullPath; Value = $result }
}

function ConvertTo-ListObservation {
    param($Value)
    $name = 'observations.list'
    Assert-ObjectShape $Value $name @('name','version','game','officialMetadataUrl','artifactUrl','artifactSizeBytes','artifactSha256','license','nsfw','metadataObservedAtUtc') @('name','version','game','officialMetadataUrl','artifactUrl','artifactSizeBytes','license','nsfw')
    Assert-String (Get-RequiredProperty $Value name $name) "$name.name" 1 256
    Assert-String (Get-RequiredProperty $Value version $name) "$name.version" 1 64
    if ((Get-RequiredProperty $Value version $name) -cne '1.3.0') { throw "$name.version must be the approved ASSOS version 1.3.0." }
    if ((Get-RequiredProperty $Value game $name) -cne 'Skyrim Special Edition') { throw "$name.game must be 'Skyrim Special Edition'." }
    Assert-HttpsUrl (Get-RequiredProperty $Value officialMetadataUrl $name) "$name.officialMetadataUrl"
    Assert-HttpsUrl (Get-RequiredProperty $Value artifactUrl $name) "$name.artifactUrl"
    Assert-NonNegativeInteger (Get-RequiredProperty $Value artifactSizeBytes $name) "$name.artifactSizeBytes"
    if ([long](Get-RequiredProperty $Value artifactSizeBytes $name) -ne 15012480) { throw "$name.artifactSizeBytes does not match approved ASSOS 1.3.0 metadata (15012480)." }
    Assert-String (Get-RequiredProperty $Value license $name) "$name.license" 1 128
    Assert-Boolean (Get-RequiredProperty $Value nsfw $name) "$name.nsfw"
    if ((Get-RequiredProperty $Value nsfw $name) -ne $false) { throw "$name.nsfw does not match approved non-NSFW metadata." }
    $hash = Get-OptionalProperty $Value artifactSha256
    if ($null -ne $hash) { Assert-Sha256 $hash "$name.artifactSha256" }
    $observedAt = Get-OptionalProperty $Value metadataObservedAtUtc
    if ($null -ne $observedAt) { Assert-UtcTime $observedAt "$name.metadataObservedAtUtc" }
    return $Value
}

function ConvertTo-WabbajackObservation {
    param($Value)
    $name = 'observations.wabbajack'
    Assert-ObjectShape $Value $name @('launcherVersion','coreVersion','executableSha256','officialReleaseUrl') @('launcherVersion','executableSha256','officialReleaseUrl')
    Assert-String (Get-RequiredProperty $Value launcherVersion $name) "$name.launcherVersion" 1 64
    if ((Get-RequiredProperty $Value launcherVersion $name) -cne '4.2.1.4') { throw "$name.launcherVersion must be the approved version 4.2.1.4." }
    $core = Get-OptionalProperty $Value coreVersion
    if ($null -ne $core) { Assert-String $core "$name.coreVersion" 1 64 }
    Assert-Sha256 (Get-RequiredProperty $Value executableSha256 $name) "$name.executableSha256"
    Assert-HttpsUrl (Get-RequiredProperty $Value officialReleaseUrl $name) "$name.officialReleaseUrl"
    $releaseUri = [Uri](Get-RequiredProperty $Value officialReleaseUrl $name)
    if ($releaseUri.DnsSafeHost -cne 'github.com' -or $releaseUri.AbsolutePath -notmatch '(?i)^/wabbajack-tools/wabbajack/releases/') {
        throw "$name.officialReleaseUrl must identify the official Wabbajack GitHub release."
    }
    return $Value
}

function ConvertTo-PathsObservation {
    param($Value)
    $name = 'observations.paths'
    Assert-ObjectShape $Value $name @('labRoot','wabbajackRoot','downloadRoot','installationRoot','gridDataRoot','evidenceRoot','repositoryEvidenceMirror') @('labRoot','wabbajackRoot','downloadRoot','installationRoot','gridDataRoot','evidenceRoot')
    $paths = [ordered]@{}
    foreach ($property in @('labRoot','wabbajackRoot','downloadRoot','installationRoot','gridDataRoot','evidenceRoot')) {
        $paths[$property] = ConvertTo-CanonicalWindowsPath (Get-RequiredProperty $Value $property $name) "$name.$property"
    }
    if (-not [string]::Equals($paths.labRoot, $script:LabRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "$name.labRoot must be '$script:LabRoot'." }
    $expected = [ordered]@{
        wabbajackRoot = 'C:\Grid-Test-Lab\tools\wabbajack\4.2.1.4'
        downloadRoot = 'C:\Grid-Test-Lab\downloads\assos\1.3.0'
        installationRoot = 'C:\Grid-Test-Lab\installations\assos\1.3.0'
        gridDataRoot = "C:\Grid-Test-Lab\grid-data\$LabId"
        evidenceRoot = "C:\Grid-Test-Lab\evidence\$LabId"
    }
    foreach ($property in $expected.Keys) {
        if (-not [string]::Equals($paths[$property], $expected[$property], [StringComparison]::OrdinalIgnoreCase)) {
            throw "$name.$property must be '$($expected[$property])'."
        }
    }
    $mirror = Get-OptionalProperty $Value repositoryEvidenceMirror
    if ($null -ne $mirror) {
        Assert-String $mirror "$name.repositoryEvidenceMirror" 1 1024
        if ($mirror -notmatch '^artifacts[\\/]test-lab[\\/]' -or $mirror -match '(?:^|[\\/])\.\.?(?:$|[\\/])') {
            throw "$name.repositoryEvidenceMirror must be a descendant of artifacts/test-lab without dot segments."
        }
        $paths.repositoryEvidenceMirror = $mirror.Replace('/', '\')
    }
    return [pscustomobject]$paths
}

function ConvertTo-RuntimeObservation {
    param($Value)
    $name = 'observations.runtime'
    Assert-ObjectShape $Value $name @('gameVersion','language','prerequisiteStatus','creationContentStatus','notes') @('gameVersion','language','prerequisiteStatus')
    Assert-String (Get-RequiredProperty $Value gameVersion $name) "$name.gameVersion" 1 64
    Assert-String (Get-RequiredProperty $Value language $name) "$name.language" 1 64
    if ((Get-RequiredProperty $Value prerequisiteStatus $name) -notin @('passed-read-only','blocked','not-observed')) { throw "$name.prerequisiteStatus is invalid." }
    $creation = Get-OptionalProperty $Value creationContentStatus
    if ($null -ne $creation -and $creation -notin @('present','incomplete','not-observed')) { throw "$name.creationContentStatus is invalid." }
    $notes = Get-OptionalProperty $Value notes
    if ($null -ne $notes) {
        $rows = Assert-Array $notes "$name.notes" 0 128
        for ($i = 0; $i -lt $rows.Count; $i++) { Assert-String $rows[$i] "$name.notes[$i]" 0 2048 }
    }
    return $Value
}

function ConvertTo-BlockerObservation {
    param($Value)
    $name = 'observations.blocker'
    Assert-ObjectShape $Value $name @('phase','code','summary','expected','observed','requiresAdditionalApproval','stoppedAtUtc') @('phase','code','summary','requiresAdditionalApproval')
    if ((Get-RequiredProperty $Value phase $name) -notin @('metadata','prerequisites','artifact','download','installation','native-launch','snapshot')) { throw "$name.phase is invalid." }
    $code = Get-RequiredProperty $Value code $name
    Assert-String $code "$name.code" 3 128
    if ($code -cnotmatch '^[a-z0-9][a-z0-9.-]{2,127}$') { throw "$name.code has an invalid stable-code format." }
    Assert-String (Get-RequiredProperty $Value summary $name) "$name.summary" 0 2048
    foreach ($property in @('expected','observed')) {
        $text = Get-OptionalProperty $Value $property
        if ($null -ne $text) { Assert-String $text "$name.$property" 0 2048 }
    }
    Assert-Boolean (Get-RequiredProperty $Value requiresAdditionalApproval $name) "$name.requiresAdditionalApproval"
    $stopped = Get-OptionalProperty $Value stoppedAtUtc
    if ($null -ne $stopped) { Assert-UtcTime $stopped "$name.stoppedAtUtc" }
    return $Value
}

function ConvertTo-Mo2Observation {
    param($Value)
    $name = 'observations.mo2'
    Assert-ObjectShape $Value $name @('version','portable','profiles','selectedProfileEvidence','nativeLaunch') @('version','portable','profiles','selectedProfileEvidence','nativeLaunch')
    Assert-String (Get-RequiredProperty $Value version $name) "$name.version" 1 64
    Assert-Boolean (Get-RequiredProperty $Value portable $name) "$name.portable"
    $profiles = Assert-Array (Get-RequiredProperty $Value profiles $name) "$name.profiles" 0 4096
    $profileNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    for ($i = 0; $i -lt $profiles.Count; $i++) {
        $profileName = "$name.profiles[$i]"
        Assert-ObjectShape $profiles[$i] $profileName @('name','modCount','separatorCount','pluginCount') @('name','modCount','separatorCount','pluginCount')
        $observedName = Get-RequiredProperty $profiles[$i] name $profileName
        Assert-String $observedName "$profileName.name" 1 255
        if (-not $profileNames.Add($observedName)) { throw "$name.profiles contains duplicate name '$observedName'." }
        foreach ($property in @('modCount','separatorCount','pluginCount')) { Assert-NonNegativeInteger (Get-RequiredProperty $profiles[$i] $property $profileName) "$profileName.$property" }
    }
    $selected = Get-RequiredProperty $Value selectedProfileEvidence $name
    Assert-String $selected "$name.selectedProfileEvidence" 0 255 -AllowNull
    $launch = Get-RequiredProperty $Value nativeLaunch $name
    Assert-ObjectShape $launch "$name.nativeLaunch" @('status','observedAtUtc','observedProfile','notes') @('status')
    if ((Get-RequiredProperty $launch status "$name.nativeLaunch") -notin @('not-attempted','verified','failed','canceled')) { throw "$name.nativeLaunch.status is invalid." }
    $observedAt = Get-OptionalProperty $launch observedAtUtc
    if ($null -ne $observedAt) { Assert-UtcTime $observedAt "$name.nativeLaunch.observedAtUtc" }
    $observedProfile = Get-OptionalProperty $launch observedProfile
    if ($null -ne $observedProfile) { Assert-String $observedProfile "$name.nativeLaunch.observedProfile" 0 255 }
    $notes = Get-OptionalProperty $launch notes
    if ($null -ne $notes) {
        $rows = Assert-Array $notes "$name.nativeLaunch.notes" 0 128
        for ($i = 0; $i -lt $rows.Count; $i++) { Assert-String $rows[$i] "$name.nativeLaunch.notes[$i]" 0 2048 }
    }
    return $Value
}

function ConvertTo-InventoryObservation {
    param($Value)
    $name = 'observations.inventory'
    $properties = @('profileCount','modDirectoryCount','separatorCount','pluginCount','archiveCount','downloadCount','configuredToolCount')
    Assert-ObjectShape $Value $name $properties $properties
    foreach ($property in $properties) { Assert-NonNegativeInteger (Get-RequiredProperty $Value $property $name) "$name.$property" }
    return $Value
}

function ConvertTo-ImportantHashesObservation {
    param($Value)
    $name = 'observations.importantHashes'
    $rows = Assert-Array $Value $name 0 100000
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $rowName = "$name[$i]"
        Assert-ObjectShape $rows[$i] $rowName @('relativePath','sha256','length') @('relativePath','sha256','length')
        $relativePath = Get-RequiredProperty $rows[$i] relativePath $rowName
        Assert-String $relativePath "$rowName.relativePath" 1 32767
        if ($relativePath -match '^(?:[A-Za-z]:|\\\\|/)' -or $relativePath -match '(?:^|[\\/])\.\.?(?:$|[\\/])') { throw "$rowName.relativePath must be a contained relative path." }
        if (-not $paths.Add($relativePath.Replace('\','/'))) { throw "$name contains duplicate relativePath '$relativePath'." }
        Assert-Sha256 (Get-RequiredProperty $rows[$i] sha256 $rowName) "$rowName.sha256"
        Assert-NonNegativeInteger (Get-RequiredProperty $rows[$i] length $rowName) "$rowName.length"
    }
    return ,$rows
}

function ConvertTo-DomainsObservation {
    param($Value)
    $name = 'observations.archiveSourceDomains'
    $rows = Assert-Array $Value $name 0 4096
    $domains = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    for ($i = 0; $i -lt $rows.Count; $i++) {
        Assert-String $rows[$i] "$name[$i]" 1 253
        if ($rows[$i] -cnotmatch '^[A-Za-z0-9.-]+$' -or $rows[$i].StartsWith('.') -or $rows[$i].EndsWith('.') -or $rows[$i].Contains('..')) { throw "$name[$i] is not a valid origin host name." }
        if (-not $domains.Add($rows[$i])) { throw "$name contains duplicate domain '$($rows[$i])'." }
    }
    return ,$rows
}

function ConvertTo-DiskUsageObservation {
    param($Value)
    $name = 'observations.diskUsage'
    $rows = Assert-Array $Value $name 0 32
    $roles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $rowName = "$name[$i]"
        Assert-ObjectShape $rows[$i] $rowName @('role','bytes','fileCount','directoryCount') @('role','bytes','fileCount','directoryCount')
        $role = Get-RequiredProperty $rows[$i] role $rowName
        if ($role -notin @('tools','downloads','installation','grid-data','evidence')) { throw "$rowName.role is invalid." }
        if (-not $roles.Add($role)) { throw "$name contains duplicate role '$role'." }
        foreach ($property in @('bytes','fileCount','directoryCount')) { Assert-NonNegativeInteger (Get-RequiredProperty $rows[$i] $property $rowName) "$rowName.$property" }
    }
    return ,$rows
}

function ConvertTo-CleanupObservation {
    param($Value, $Paths)
    $name = 'observations.cleanup'
    Assert-ObjectShape $Value $name @('automatic','ownedRoots','excludedRoots','instructions') @('automatic','ownedRoots','excludedRoots')
    Assert-Boolean (Get-RequiredProperty $Value automatic $name) "$name.automatic"
    if ((Get-RequiredProperty $Value automatic $name) -ne $false) { throw "$name.automatic must be false." }
    $owned = Assert-Array (Get-RequiredProperty $Value ownedRoots $name) "$name.ownedRoots" 1 32
    $ownedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $approvedOwned = @($Paths.wabbajackRoot,$Paths.downloadRoot,$Paths.installationRoot,$Paths.gridDataRoot,$Paths.evidenceRoot)
    if ($null -ne $Paths.PSObject.Properties['repositoryEvidenceMirror']) {
        $approvedOwned += [IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot $Paths.repositoryEvidenceMirror)).TrimEnd('\')
    }
    foreach ($root in $owned) {
        $canonical = ConvertTo-CanonicalWindowsPath $root "$name.ownedRoots"
        if ($canonical -notin $approvedOwned) { throw "$name.ownedRoots contains an unapproved root '$canonical'." }
        if (-not $ownedSet.Add($canonical)) { throw "$name.ownedRoots contains duplicate root '$canonical'." }
    }
    $excluded = Assert-Array (Get-RequiredProperty $Value excludedRoots $name) "$name.excludedRoots" 1 32
    $excludedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($root in $excluded) {
        $canonical = ConvertTo-CanonicalWindowsPath $root "$name.excludedRoots"
        if (-not $excludedSet.Add($canonical)) { throw "$name.excludedRoots contains duplicate root '$canonical'." }
        foreach ($ownedRoot in $ownedSet) {
            if ((Test-IsSameOrDescendant $ownedRoot $canonical) -or (Test-IsSameOrDescendant $canonical $ownedRoot)) { throw "$name has overlapping owned and excluded roots." }
        }
    }
    $localGridRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Grid'
    foreach ($requiredRoot in @('D:\','C:\Wabbajack',$localGridRoot)) {
        if (-not $excludedSet.Contains($requiredRoot)) { throw "$name.excludedRoots must include '$requiredRoot'." }
    }
    $instructions = Get-OptionalProperty $Value instructions
    if ($null -ne $instructions) {
        $rows = Assert-Array $instructions "$name.instructions" 0 64
        for ($i = 0; $i -lt $rows.Count; $i++) { Assert-String $rows[$i] "$name.instructions[$i]" 0 2048 }
    }
    return $Value
}

function Assert-StateEvidence {
    param($Manifest)

    $blocker = Get-OptionalProperty $Manifest blocker
    $mo2 = Get-OptionalProperty $Manifest mo2
    $inventory = Get-OptionalProperty $Manifest inventory
    $importantHashes = Get-OptionalProperty $Manifest importantHashes
    $diskUsage = Get-OptionalProperty $Manifest diskUsage
    $hasBlocker = $null -ne $blocker
    $hasMo2 = $null -ne $mo2
    $hasInventory = $null -ne $inventory
    $hasHashes = $null -ne $importantHashes
    $hasDisk = $null -ne $diskUsage
    $hasArtifactHash = $null -ne $Manifest.list.PSObject.Properties['artifactSha256']

    if ($Manifest.state -eq 'blocked') {
        if (-not $hasBlocker) { throw "State 'blocked' requires explicit blocker evidence." }
    }
    elseif ($hasBlocker) { throw "State '$($Manifest.state)' must not contain blocker evidence." }

    if ($Manifest.state -in @('downloaded','installed','native-mo2-verified') -and -not $hasArtifactHash) {
        throw "State '$($Manifest.state)' requires an explicitly observed list artifact SHA-256."
    }
    if ($Manifest.state -in @('installed','native-mo2-verified')) {
        if ($Manifest.runtime.prerequisiteStatus -ne 'passed-read-only') { throw "State '$($Manifest.state)' requires read-only prerequisites to have passed." }
        if ($null -eq $Manifest.wabbajack.PSObject.Properties['coreVersion']) { throw "State '$($Manifest.state)' requires the separately observed Wabbajack core version." }
    }
    if ($Manifest.state -in @('installed','native-mo2-verified')) {
        if (-not ($hasMo2 -and $hasInventory -and $hasHashes -and $hasDisk)) { throw "State '$($Manifest.state)' requires MO2, inventory, important-hash, and disk-usage observations." }
        $ownedRoots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($root in $Manifest.cleanup.ownedRoots) { [void]$ownedRoots.Add($root) }
        foreach ($requiredRoot in @($Manifest.paths.wabbajackRoot,$Manifest.paths.downloadRoot,$Manifest.paths.installationRoot,$Manifest.paths.gridDataRoot,$Manifest.paths.evidenceRoot)) {
            if (-not $ownedRoots.Contains($requiredRoot)) { throw "State '$($Manifest.state)' requires cleanup ownership for '$requiredRoot'." }
        }
        if (-not $mo2.portable) { throw "State '$($Manifest.state)' requires explicit portable MO2 evidence." }
        if (@($mo2.profiles).Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$mo2.selectedProfileEvidence)) { throw "State '$($Manifest.state)' requires profiles and selected-profile evidence." }
        if ([long]$inventory.profileCount -ne @($mo2.profiles).Count) { throw 'inventory.profileCount must equal the number of observed MO2 profiles.' }
        $importantPaths = @($importantHashes | ForEach-Object { $_.relativePath.Replace('\','/') })
        foreach ($requiredPath in @('ModOrganizer.exe','ModOrganizer.ini')) {
            if ($requiredPath -notin $importantPaths) { throw "Installed evidence must include an important hash for '$requiredPath'." }
        }
        $roles = @($diskUsage | ForEach-Object role)
        foreach ($requiredRole in @('tools','downloads','installation','grid-data','evidence')) {
            if ($requiredRole -notin $roles) { throw "Installed evidence must include disk usage for role '$requiredRole'." }
        }
    }
    if ($Manifest.state -eq 'native-mo2-verified') {
        if ($mo2.nativeLaunch.status -ne 'verified') { throw "State 'native-mo2-verified' requires nativeLaunch.status 'verified'." }
        if ($null -eq $mo2.nativeLaunch.PSObject.Properties['observedAtUtc'] -or [string]::IsNullOrWhiteSpace([string]$mo2.nativeLaunch.observedProfile)) {
            throw "State 'native-mo2-verified' requires native-launch time and observed-profile evidence."
        }
        if (-not [string]::Equals($mo2.selectedProfileEvidence, $mo2.nativeLaunch.observedProfile, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The native-launch observed profile must match selectedProfileEvidence.'
        }
    }
    elseif ($hasMo2 -and $mo2.nativeLaunch.status -eq 'verified' -and $Manifest.state -ne 'retired' -and $Manifest.state -ne 'blocked') {
        throw "Verified native-launch evidence requires state 'native-mo2-verified'."
    }

    if ($Manifest.state -eq 'blocked') {
        $phase = $blocker.phase
        if ($phase -eq 'prerequisites') {
            if ($Manifest.runtime.prerequisiteStatus -ne 'blocked') { throw 'A prerequisite blocker requires runtime.prerequisiteStatus blocked.' }
            if (-not $blocker.requiresAdditionalApproval) { throw 'A prerequisite blocker must require additional approval.' }
        }
        if ($phase -in @('download','installation','native-launch','snapshot') -and -not $hasArtifactHash) { throw "A '$phase' blocker requires the previously observed artifact SHA-256." }
        if ($phase -in @('native-launch','snapshot') -and -not ($hasMo2 -and $hasInventory -and $hasHashes -and $hasDisk)) { throw "A '$phase' blocker requires installed MO2 evidence." }
        if ($phase -in @('native-launch','snapshot') -and $Manifest.runtime.prerequisiteStatus -ne 'passed-read-only') { throw "A '$phase' blocker requires read-only prerequisites to have passed." }
    }
}

function Assert-OutputLocation {
    param([string]$Path, $Paths)

    if ($Path -match '^[dD]:[\\/]') { throw 'OutputPath must never refer to drive D:.' }
    $fullPath = [IO.Path]::GetFullPath($Path)
    $boundary = $Paths.evidenceRoot
    $allowed = Test-IsSameOrDescendant $fullPath $boundary
    if (-not $allowed -and $null -ne $Paths.PSObject.Properties['repositoryEvidenceMirror']) {
        $declaredMirror = [IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot $Paths.repositoryEvidenceMirror)).TrimEnd('\')
        $allowed = Test-IsSameOrDescendant $fullPath $declaredMirror
        if ($allowed) { $boundary = $declaredMirror }
    }
    if (-not $allowed) { throw 'OutputPath must be under the manifest evidence root or ignored artifacts/test-lab.' }
    $current = Split-Path -Parent $fullPath
    while (Test-IsSameOrDescendant $current $boundary) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "OutputPath traverses a reparse point: '$current'." }
        }
        if ([string]::Equals($current, $boundary, [StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or [string]::Equals($parent, $current, [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = $parent
    }
    return $fullPath
}

function Write-AtomicUtf8Json {
    param([string]$Path, [string]$Json)

    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporaryPath = Join-Path $directory ('.' + [IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $backupPath = Join-Path $directory ('.' + [IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.bak')
    try {
        [IO.File]::WriteAllText($temporaryPath, $Json, [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporaryPath, $Path, $backupPath) }
        else { [IO.File]::Move($temporaryPath, $Path) }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
        if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Force }
    }
}

Assert-UtcTime $CreatedAtUtc 'CreatedAtUtc'
if (-not [string]::IsNullOrWhiteSpace($UpdatedAtUtc)) {
    Assert-UtcTime $UpdatedAtUtc 'UpdatedAtUtc'
    if ([DateTimeOffset]::Parse($UpdatedAtUtc) -lt [DateTimeOffset]::Parse($CreatedAtUtc)) { throw 'UpdatedAtUtc must not precede CreatedAtUtc.' }
}

$inputDocuments = [Collections.Generic.List[object]]::new()
if ($PSCmdlet.ParameterSetName -eq 'Combined') {
    $inputDocument = Read-ObservationJson -Path $ObservationPath
    $inputDocuments.Add($inputDocument)
    $observations = $inputDocument.Value
}
else {
    $baseDocument = Read-ObservationJson -Path $BaseObservationPath
    $reportDocument = Read-ObservationJson -Path $PrerequisiteReportPath
    $inputDocuments.Add($baseDocument)
    $inputDocuments.Add($reportDocument)
    $base = $baseDocument.Value
    $report = $reportDocument.Value
    Assert-ObjectShape $base 'baseObservations' @('list','wabbajack','paths','cleanup') @('list','wabbajack','paths','cleanup')
    Assert-ObjectShape $report 'prerequisiteReport' @('schemaVersion','kind','observedAtUtc','artifact','artifactEvidencePath','externalGameRoot','archiveSourceDomains','runtime','checks','warnings','blocker') @('schemaVersion','kind','observedAtUtc','artifact','archiveSourceDomains','runtime','checks','warnings')
    if ((Get-RequiredProperty $report schemaVersion 'prerequisiteReport') -ne 1 -or (Get-RequiredProperty $report kind 'prerequisiteReport') -cne 'grid-test-lab-prerequisite-report') {
        throw 'PrerequisiteReportPath does not contain a supported version 1 prerequisite report.'
    }
    Assert-UtcTime (Get-RequiredProperty $report observedAtUtc 'prerequisiteReport') 'prerequisiteReport.observedAtUtc'
    $artifact = Get-RequiredProperty $report artifact 'prerequisiteReport'
    Assert-ObjectShape $artifact 'prerequisiteReport.artifact' @('path','exists','length','sha256') @('path','exists','length','sha256')
    Assert-Boolean (Get-RequiredProperty $artifact exists 'prerequisiteReport.artifact') 'prerequisiteReport.artifact.exists'
    $artifactLength = Get-RequiredProperty $artifact length 'prerequisiteReport.artifact'
    $artifactHash = Get-RequiredProperty $artifact sha256 'prerequisiteReport.artifact'
    if ($null -ne $artifactLength) { Assert-NonNegativeInteger $artifactLength 'prerequisiteReport.artifact.length' }
    if ($null -ne $artifactHash) {
        Assert-Sha256 $artifactHash 'prerequisiteReport.artifact.sha256'
        if ($null -eq $artifactLength -or [long]$artifactLength -ne 15012480) { throw 'A prerequisite-report artifact hash requires the approved observed artifact length.' }
        if ($null -ne $base.list.PSObject.Properties['artifactSha256'] -and $base.list.artifactSha256 -cne $artifactHash) { throw 'Base and prerequisite-report artifact hashes disagree.' }
    }
    $combined = [ordered]@{
        list = $base.list
        wabbajack = $base.wabbajack
        paths = $base.paths
        runtime = Get-RequiredProperty $report runtime 'prerequisiteReport'
        archiveSourceDomains = Get-RequiredProperty $report archiveSourceDomains 'prerequisiteReport'
        cleanup = $base.cleanup
    }
    $reportedBlocker = Get-OptionalProperty $report blocker
    if ($null -ne $reportedBlocker) { $combined.blocker = $reportedBlocker }
    $reportedWarnings = Get-RequiredProperty $report warnings 'prerequisiteReport'
    if (@($reportedWarnings).Count -gt 0) { $combined.warnings = $reportedWarnings }
    $observations = [pscustomobject]$combined
}
$topLevelAllowed = @('list','wabbajack','paths','runtime','archiveSourceDomains','cleanup','blocker','mo2','inventory','importantHashes','diskUsage','warnings')
$topLevelRequired = @('list','wabbajack','paths','runtime','archiveSourceDomains','cleanup')
Assert-ObjectShape $observations 'observations' $topLevelAllowed $topLevelRequired

$paths = ConvertTo-PathsObservation (Get-RequiredProperty $observations paths 'observations')
foreach ($document in $inputDocuments) {
    $observationAllowed = Test-IsSameOrDescendant $document.Path $paths.evidenceRoot
    if (-not $observationAllowed -and $null -ne $paths.PSObject.Properties['repositoryEvidenceMirror']) {
        $declaredMirror = [IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot $paths.repositoryEvidenceMirror)).TrimEnd('\')
        $observationAllowed = Test-IsSameOrDescendant $document.Path $declaredMirror
    }
    if (-not $observationAllowed) { throw 'Every observation input must be under the declared evidence root or repository evidence mirror.' }
}

$manifest = [ordered]@{
    schemaVersion = 1
    labId = $LabId
    state = $State
    createdAtUtc = $CreatedAtUtc
    list = ConvertTo-ListObservation (Get-RequiredProperty $observations list 'observations')
    wabbajack = ConvertTo-WabbajackObservation (Get-RequiredProperty $observations wabbajack 'observations')
    paths = $paths
    runtime = ConvertTo-RuntimeObservation (Get-RequiredProperty $observations runtime 'observations')
    archiveSourceDomains = ConvertTo-DomainsObservation (Get-RequiredProperty $observations archiveSourceDomains 'observations')
    cleanup = ConvertTo-CleanupObservation (Get-RequiredProperty $observations cleanup 'observations') $paths
}
if (-not [string]::IsNullOrWhiteSpace($UpdatedAtUtc)) { $manifest.updatedAtUtc = $UpdatedAtUtc }

foreach ($property in @('blocker','mo2','inventory','importantHashes','diskUsage','warnings')) {
    $value = Get-OptionalProperty $observations $property
    if ($null -eq $value) { continue }
    switch ($property) {
        blocker { $manifest.blocker = ConvertTo-BlockerObservation $value }
        mo2 { $manifest.mo2 = ConvertTo-Mo2Observation $value }
        inventory { $manifest.inventory = ConvertTo-InventoryObservation $value }
        importantHashes { $manifest.importantHashes = ConvertTo-ImportantHashesObservation $value }
        diskUsage { $manifest.diskUsage = ConvertTo-DiskUsageObservation $value }
        warnings {
            $rows = Assert-Array $value 'observations.warnings' 0 1024
            for ($i = 0; $i -lt $rows.Count; $i++) { Assert-String $rows[$i] "observations.warnings[$i]" 0 2048 }
            $manifest.warnings = @($rows)
        }
    }
}

$manifestObject = [pscustomobject]$manifest
Assert-StateEvidence $manifestObject
$output = Assert-OutputLocation $OutputPath $paths
$outputOwned = $false
foreach ($ownedRoot in $manifestObject.cleanup.ownedRoots) {
    if (Test-IsSameOrDescendant $output $ownedRoot) { $outputOwned = $true; break }
}
if (-not $outputOwned) { throw 'OutputPath must be contained by an explicitly declared cleanup.ownedRoots entry.' }
foreach ($document in $inputDocuments) {
    if ([string]::Equals($output, $document.Path, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputPath must differ from every observation input path.' }
}

$json = ConvertTo-Json -InputObject $manifestObject -Depth 12
Write-AtomicUtf8Json -Path $output -Json ($json + [Environment]::NewLine)
Write-Host "Manifest: $output"
Write-Host "State: $State; lab: $LabId"
