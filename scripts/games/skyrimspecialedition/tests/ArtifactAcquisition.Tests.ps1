#requires -Version 5.1

$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $gameRoot 'health\actions\Invoke-GridSkyrimArtifactAcquisition.ps1')

function Assert-True([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected,$Actual,[string]$Message) { if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." } }
function Assert-Throws([scriptblock]$Action,[string]$Pattern,[string]$Message) { try { & $Action; throw "$Message Expected '$Pattern'." } catch { if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Unexpected: $($_.Exception.Message)" } } }

$fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fixtures\artifact-acquisition.cases.v1.json') -Raw | ConvertFrom-Json
$identity = $fixture.positive[0]
$root = Join-Path $env:TEMP ('grid-acquire-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $root $identity.fileName
$inbox = Join-Path $root 'imports\inbox'
$quarantine = Join-Path $root 'quarantine\sha256'
$record = Join-Path $root 'case\artifact-acquisition.v1.json'
New-Item -ItemType Directory -Path $root -Force | Out-Null
[IO.File]::WriteAllBytes($source,[byte[]](1,2,3,4,5,6))
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash

try {
    $verified = Invoke-GridSkyrimArtifactAcquisition -SourceLiteralPath $source -ExpectedProviderIdentity $identity -ObservedProviderIdentity $identity `
        -IdentityEvidenceIds @('provider-fixture') -InboxRoot $inbox -QuarantineRoot $quarantine -AcquisitionRecordPath $record -ExpectedSha256 $hash -Confirm:$false -PassThru
    Assert-Equal 'Verified' $verified.state 'Exact local bytes must be verified.'
    Assert-Equal $hash $verified.artifact.sha256 'Receipt must bind the full SHA-256.'
    Assert-True (Test-Path -LiteralPath $verified.artifact.path -PathType Leaf) 'Verified bytes must enter the CAS path.'
    Assert-True (Test-Path -LiteralPath $record -PathType Leaf) 'A secret-free acquisition receipt must be persisted.'

    $again = Invoke-GridSkyrimArtifactAcquisition -SourceLiteralPath $source -ExpectedProviderIdentity $identity -ObservedProviderIdentity $identity `
        -IdentityEvidenceIds @('provider-fixture') -InboxRoot $inbox -QuarantineRoot $quarantine -AcquisitionRecordPath $record -ExpectedSha256 $hash -Confirm:$false -PassThru
    Assert-Equal $verified.artifact.path $again.artifact.path 'Identical acquisition must deduplicate.'

    $wrong = $identity | Select-Object *
    $wrong.fileId = 'wrong'
    Assert-Throws { Invoke-GridSkyrimArtifactAcquisition -SourceLiteralPath $source -ExpectedProviderIdentity $identity -ObservedProviderIdentity $wrong -IdentityEvidenceIds @('e') -InboxRoot $inbox -QuarantineRoot $quarantine -AcquisitionRecordPath $record -Confirm:$false } 'ProviderIdentityConflict' 'Mismatched provider identity must fail closed.'

    $managed = Join-Path $root 'managed'
    $actionPath = Join-Path $root 'case\user-action.v1.json'
    $action = New-GridSkyrimUserAcquisitionAction -ProviderIdentity $identity -OfficialUri 'https://example.invalid/files/173454' -ManagedInboxRoot $managed -OutputPath $actionPath -IdentityEvidenceIds @('provider-fixture') -Confirm:$false -PassThru
    Assert-Equal 'PendingUserAcquisition' $action.state 'Unavailable credentials must produce a resumable user action.'
    $roundedIdentity = [pscustomobject][ordered]@{ provider='Fixture'; gameId='fixture'; modId='42'; fileId='7'; version='1.0.0'; fileName='fixture-main-file.7z'; sizeBytes=$null; displaySize='1.0 MB'; providerMetadataStatus='RoundedDisplayOnly' }
    $roundedAction = New-GridSkyrimUserAcquisitionAction -ProviderIdentity $roundedIdentity -OfficialUri 'https://example.invalid/files/7' -ManagedInboxRoot (Join-Path $root 'rounded-managed') -OutputPath (Join-Path $root 'case\rounded-user-action.v1.json') -IdentityEvidenceIds @('provider-page') -Confirm:$false -PassThru
    Assert-True ($null -eq $roundedAction.expectedSizeBytes) 'A rounded display size must remain unresolved until exact bytes or authenticated provider metadata are observed.'
    Assert-True (Test-Path -LiteralPath $action.managedInbox -PathType Container) 'User action must own one bounded inbox.'
    $stillPending = Complete-GridSkyrimManagedAcquisition -UserActionPath $actionPath -QuarantineRoot (Join-Path $root 'managed-quarantine') -AcquisitionRecordPath (Join-Path $root 'managed-receipt.json') -ExpectedSha256 $hash -Confirm:$false -PassThru
    Assert-Equal 'PendingUserAcquisition' $stillPending.state 'An empty managed inbox must remain a resumable state rather than throw or claim unavailability.'
    Assert-Equal 'ResumeAvailable' $stillPending.recoveryDisposition 'An empty managed inbox must preserve resume availability.'
    $malformedActionPath = Join-Path $root 'case\malformed-user-action.v1.json'
    $malformedAction = Get-Content -LiteralPath $actionPath -Raw | ConvertFrom-Json
    $malformedAction.PSObject.Properties.Remove('expectedSizeBytes')
    $malformedAction | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $malformedActionPath -Encoding UTF8
    Assert-Throws {
        Complete-GridSkyrimManagedAcquisition -UserActionPath $malformedActionPath -QuarantineRoot (Join-Path $root 'managed-quarantine') -AcquisitionRecordPath (Join-Path $root 'managed-receipt.json') -Confirm:$false -PassThru
    } 'UserAcquisitionActionInvalid' 'A managed-resume action missing a schema-required exact-size field must fail closed.'
    [IO.File]::WriteAllBytes((Join-Path $action.managedInbox $identity.fileName), [byte[]](1,2,3))
    Assert-Throws {
        Complete-GridSkyrimManagedAcquisition -UserActionPath $actionPath -QuarantineRoot (Join-Path $root 'managed-quarantine') -AcquisitionRecordPath (Join-Path $root 'managed-receipt.json') -Confirm:$false -PassThru
    } 'ArtifactSizeMismatch' 'A sealed exact-size action must reject a same-name managed-inbox artifact with different bytes.'
    Remove-Item -LiteralPath (Join-Path $action.managedInbox $identity.fileName) -Force
    Copy-Item -LiteralPath $source -Destination (Join-Path $action.managedInbox $identity.fileName)
    $completed = Complete-GridSkyrimManagedAcquisition -UserActionPath $actionPath -QuarantineRoot (Join-Path $root 'managed-quarantine') -AcquisitionRecordPath (Join-Path $root 'managed-receipt.json') -ExpectedSha256 $hash -Confirm:$false -PassThru
    Assert-Equal 'Verified' $completed.state 'An exact managed-inbox artifact must resume into verified quarantine.'
    Assert-Equal $action.actionId $completed.predecessorActionId 'The acquisition receipt must bind its predecessor user action.'
    Assert-True ([string]$completed.predecessorResumeTokenSha256 -match '^[A-F0-9]{64}$') 'The opaque resume token must be fingerprinted rather than copied into the receipt.'
    Assert-Equal 'Unavailable' (Get-GridSkyrimProtectedCredentialHandleState).status 'Missing protected credential must be a safe unavailable state.'
    Assert-Equal 'Unavailable' (Get-GridSkyrimProtectedCredentialHandleState -CredentialHandle 'os-protected:fixture').status 'A handle without a resolver must remain unavailable.'

    $remoteRoot = Join-Path $root 'remote'
    $remoteInbox = Join-Path $remoteRoot 'inbox'
    $remoteQuarantine = Join-Path $remoteRoot 'quarantine'
    $remoteRecord = Join-Path $remoteRoot 'receipt.json'
    $responses = 0
    $responseFactory = {
        param($Uri,$Headers,$Timeout)
        $script:responses++
        if ($script:responses -eq 1) {
            return [pscustomobject]@{ StatusCode=200; Headers=@{ ETag='"fixture-v1"'; 'Last-Modified'='Mon, 01 Sep 2026 12:00:00 GMT' }; Stream=(New-Object IO.MemoryStream(,[byte[]](1,2,3))) }
        }
        Assert-Equal 'bytes=3-' ([string]$Headers.Range) 'Resume must request only remaining bytes.'
        Assert-Equal '"fixture-v1"' ([string]$Headers.'If-Range') 'Resume must bind the unchanged strong validator.'
        [pscustomobject]@{ StatusCode=206; Headers=@{ ETag='"fixture-v1"'; 'Last-Modified'='Mon, 01 Sep 2026 12:00:00 GMT' }; Stream=(New-Object IO.MemoryStream(,[byte[]](4,5,6))) }
    }
    Assert-Throws {
        Invoke-GridSkyrimArtifactAcquisition -SourceUri 'https://example.invalid/files/fixture-main-file.7z' -AllowedProviderHosts @('example.invalid') `
            -ExpectedProviderIdentity $identity -ObservedProviderIdentity $identity -IdentityEvidenceIds @('provider-fixture') -InboxRoot $remoteInbox `
            -QuarantineRoot $remoteQuarantine -AcquisitionRecordPath $remoteRecord -ExpectedSha256 $hash -HttpResponseFactory $responseFactory -Confirm:$false
    } 'AcquisitionInterrupted' 'An interrupted response must retain resumable bytes rather than claim unavailability.'
    $resumeRecord = @(Get-ChildItem -LiteralPath $remoteInbox -Filter '*.resume.v1.json' -File)
    Assert-Equal 1 $resumeRecord.Count 'Interrupted HTTPS acquisition must persist one bounded resume record.'
    Assert-Equal 3 ([long]((Get-Content -LiteralPath $resumeRecord[0].FullName -Raw | ConvertFrom-Json).downloadedBytes)) 'Resume record must retain the exact completed byte count.'
    $resumed = Invoke-GridSkyrimArtifactAcquisition -SourceUri 'https://example.invalid/files/fixture-main-file.7z' -AllowedProviderHosts @('example.invalid') `
        -ExpectedProviderIdentity $identity -ObservedProviderIdentity $identity -IdentityEvidenceIds @('provider-fixture') -InboxRoot $remoteInbox `
        -QuarantineRoot $remoteQuarantine -AcquisitionRecordPath $remoteRecord -ExpectedSha256 $hash -HttpResponseFactory $responseFactory -Confirm:$false -PassThru
    Assert-Equal 'Verified' $resumed.state 'An unchanged strong validator must permit exact bounded resume.'
    Assert-Equal 2 $responses 'Resume must not repeat a completed source range.'

    $retryAttempts = 0
    $retryFactory = {
        param($Uri,$Headers,$Timeout)
        $script:retryAttempts++
        if ($script:retryAttempts -eq 1) { throw [IO.IOException]::new('fixture transient failure') }
        [pscustomobject]@{ StatusCode=200; Headers=@{ ETag='"retry-v1"' }; Stream=(New-Object IO.MemoryStream(,[byte[]](1,2,3,4,5,6))) }
    }
    $retryResult = Invoke-GridSkyrimArtifactAcquisition -SourceUri 'https://example.invalid/files/fixture-main-file.7z' -AllowedProviderHosts @('example.invalid') `
        -ExpectedProviderIdentity $identity -ObservedProviderIdentity $identity -IdentityEvidenceIds @('provider-fixture') -InboxRoot (Join-Path $root 'retry\inbox') `
        -QuarantineRoot (Join-Path $root 'retry\quarantine') -AcquisitionRecordPath (Join-Path $root 'retry\receipt.json') -ExpectedSha256 $hash `
        -HttpResponseFactory $retryFactory -MaximumTransientRetries 1 -Confirm:$false -PassThru
    Assert-Equal 'Verified' $retryResult.state 'One bounded transient transport retry must be recoverable.'
    Assert-Equal 2 $retryAttempts 'Transient transport retry must occur no more than once.'

    $notFoundFactory = { param($Uri,$Headers,$Timeout) [pscustomobject]@{ StatusCode=404; Headers=@{}; Stream=(New-Object IO.MemoryStream) } }
    Assert-Throws {
        Invoke-GridSkyrimArtifactAcquisition -SourceUri 'https://example.invalid/files/missing.7z' -AllowedProviderHosts @('example.invalid') `
            -ExpectedProviderIdentity $identity -ObservedProviderIdentity $identity -IdentityEvidenceIds @('provider-fixture') -InboxRoot (Join-Path $root 'missing\inbox') `
            -QuarantineRoot (Join-Path $root 'missing\quarantine') -AcquisitionRecordPath (Join-Path $root 'missing\receipt.json') -HttpResponseFactory $notFoundFactory -Confirm:$false
    } 'ExactArtifactSourceNotFound' 'One exact 404 is a source observation, not the terminal external-dependency result.'

    $redacted = ConvertTo-GridSkyrimRedactedValue ([pscustomobject]@{ Authorization='Bearer secret'; uri='https://user:secret@example.invalid/file?access_token=secret'; nested=@{ apiKey='secret' } })
    $redactedJson = $redacted | ConvertTo-Json -Depth 8 -Compress
    Assert-True ($redactedJson -notmatch 'Bearer secret|user:secret|access_token=secret|"secret"') 'Receipts must redact credentials, URI user info, and token queries.'

    $notYet = Resolve-GridSkyrimArtifactAcquisitionOutcome -ProviderIdentityStatus Verified -SourceObservations @([pscustomobject]@{status='Pending'})
    Assert-Equal 'AcquisitionIncomplete' $notYet.status 'A pending source must not be mislabeled unavailable.'
    $unavailable = Resolve-GridSkyrimArtifactAcquisitionOutcome -ProviderIdentityStatus Verified -SourceObservations @([pscustomobject]@{status='ExactNotFound'},[pscustomobject]@{status='TerminalUnavailable'})
    Assert-Equal 'ExactArtifactUnavailable' $unavailable.status 'Only sealed identity plus terminal source observations may emit exact unavailability.'
    Assert-Equal 'ExternalDependencyRequired' $unavailable.recoveryDisposition 'External dependency taxonomy is reserved for the proven exact unavailable artifact.'

    $maximum = (Get-Command Invoke-GridSkyrimArtifactAcquisition).Parameters['MaximumBytes'].Attributes | Where-Object { $_ -is [Management.Automation.ValidateRangeAttribute] }
    Assert-Equal 1073741824 ([long]$maximum.MaxRange) 'The absolute acquisition ceiling must be exactly 1 GiB.'
    Write-Host 'PASS: artifact acquisition validates provider identity, resumes through bounded state, quarantines by digest, redacts secrets, and preserves exact unavailability taxonomy.'
}
finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
