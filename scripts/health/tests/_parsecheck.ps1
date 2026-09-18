param([Parameter(Mandatory)][string[]]$Path)
$hadErrors = $false
$files = @($Path | ForEach-Object {
    if (Test-Path -LiteralPath $_ -PathType Container) {
        Get-ChildItem -LiteralPath $_ -Recurse -File | Where-Object { $_.Extension -in @('.ps1', '.psm1', '.psd1') } | Select-Object -ExpandProperty FullName
    }
    else { [IO.Path]::GetFullPath($_) }
} | Sort-Object -Unique)
foreach ($p in $files) {
    $errors = $null
    $tokens = $null
    [System.Management.Automation.Language.Parser]::ParseFile($p, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) {
        $hadErrors = $true
        Write-Host "PARSE ERRORS: $p"
        $errors | ForEach-Object { Write-Host "  $($_.Message) at line $($_.Extent.StartLineNumber)" }
    } else {
        Write-Host "OK: $p"
    }
}
if ($hadErrors) { exit 1 }
