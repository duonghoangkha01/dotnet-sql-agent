# Creates .env from .env.example with a random secret in place of every __GENERATE__.
# Refuses to overwrite an existing .env unless -Force is given.
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$example = Join-Path $root '.env.example'
$target = Join-Path $root '.env'

if ((Test-Path $target) -and -not $Force) {
    Write-Error ".env already exists. Use -Force to regenerate (this rotates every secret)."
}

# Alphanumeric only, so values are safe in sqlcmd variables and shells without quoting.
# SQL Server requires upper + lower + digit, so retry until all three are present.
function New-Secret([int]$Length) {
    $chars = [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    while ($true) {
        $value = -join (1..$Length | ForEach-Object {
            $chars[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($chars.Length)]
        })
        if ($value -cmatch '[A-Z]' -and $value -cmatch '[a-z]' -and $value -match '[0-9]') { return $value }
    }
}

$lines = Get-Content $example | ForEach-Object {
    if ($_ -match '^(?<key>[A-Z0-9_]+)=__GENERATE__$') {
        $length = if ($Matches.key -eq 'JWT_SIGNING_KEY') { 48 } else { 32 }
        "$($Matches.key)=$(New-Secret $length)"
    } else {
        $_
    }
}

# Never report success with a placeholder left behind: a leftover JWT key would be a public constant.
if ($lines -match '=__GENERATE__') {
    Write-Error "Some __GENERATE__ placeholders were not replaced. Check .env.example."
}

# LF line endings: compose and the shell scripts read this file on Linux too.
[System.IO.File]::WriteAllText($target, (($lines -join "`n") + "`n"))
Write-Host "Wrote .env with generated secrets (git-ignored)."
