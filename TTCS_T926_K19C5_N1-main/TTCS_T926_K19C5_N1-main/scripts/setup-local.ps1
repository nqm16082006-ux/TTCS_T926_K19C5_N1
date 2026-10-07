param([string]$Workspace = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$template = Join-Path $Workspace '.env.example'
$destination = Join-Path $Workspace '.env'
if (Test-Path -LiteralPath $destination) {
    Write-Host '.env already exists; existing configuration preserved.'
    exit 0
}
if (!(Test-Path -LiteralPath $template)) { throw 'Missing .env.example' }
function New-LocalSecret {
    $bytes = New-Object byte[] 32
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return [Convert]::ToBase64String($bytes)
}
$content = Get-Content -LiteralPath $template -Raw
$content = $content.Replace('CHANGE_ME_DATABASE_PASSWORD', (New-LocalSecret))
$content = $content.Replace('CHANGE_ME_JWT_KEY', (New-LocalSecret))
[System.IO.File]::WriteAllText($destination, $content, [System.Text.UTF8Encoding]::new($false))
Write-Host 'Created local .env with random secrets. Keep this file private.'
