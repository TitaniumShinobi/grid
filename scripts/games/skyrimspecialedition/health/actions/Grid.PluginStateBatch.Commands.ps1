#requires -Version 5.1

<#!
.SYNOPSIS
Exposes the plugin-state batch executable through the Skyrim health module.
.DESCRIPTION
The underlying file remains a directly invokable, parameter-bound action
script for tests and automation. This library wrapper prevents module import
from executing that action or requesting its mandatory mutation parameters.
#>

$script:GridPluginStateBatchExecutorPath = Join-Path $PSScriptRoot 'Invoke-GridAuthorizedPluginStateBatch.ps1'

function Invoke-GridAuthorizedPluginStateBatch {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory)][string]$SpecificationPath,
        [Parameter(Mandatory)][string]$CurrentContextFingerprint,
        [Parameter(Mandatory)][string]$CurrentEvidenceFingerprint,
        [Parameter(Mandatory)][string]$AuthorizationGrantId,
        [Parameter(Mandatory)][string]$AuthorizationSecret,
        [Parameter(Mandatory)][string]$AuthorizationStoreRoot,
        [Parameter(Mandatory)]$SemanticBinding,
        [Parameter(Mandatory)][string]$CaseStoreRoot,
        [Parameter(Mandatory)][string]$TransactionRoot,
        [switch]$PassThru
    )

    & $script:GridPluginStateBatchExecutorPath @PSBoundParameters
}
