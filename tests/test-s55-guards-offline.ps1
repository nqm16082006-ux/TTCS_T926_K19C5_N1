<#
.SYNOPSIS
    S-55: BO KIEM THU OFFLINE CHO CAC GUARD H1 / H2 / H3

.DESCRIPTION
    - H1: Deny-by-default host guard cua restore drill (allowlist, cloud block, prefix, source conflict).
    - H2: Xac minh manifest + SHA-256 truoc khi restore (ca Assert-BackupArchiveIntegrity lan Test-S55BackupValidity).
    - H3: Timeout an toan cho tien trinh (Invoke-ProcessWithTimeout).
    100% offline, khong Docker, khong DB, khong credentials.
#>

$ErrorActionPreference = 'Stop'

Write-Host "=================================================================="
Write-Host " S-55: KIEM THU GUARD H1 (HOST) / H2 (ARCHIVE) / H3 (TIMEOUT)"
Write-Host "=================================================================="

$scriptDir = Split-Path $PSScriptRoot -Parent
$restoreScript = Join-Path $scriptDir "scripts\restore-drill.ps1"
$commonScript = Join-Path $scriptDir "scripts\s55-common.ps1"

$passCount = 0
$failCount = 0

function Run-GuardTest([string]$TestName, [scriptblock]$TestBlock) {
    try {
        & $TestBlock
        Write-Host " [PASS] $TestName" -ForegroundColor Green
        $script:passCount++
    } catch {
        Write-Host " [FAIL] $TestName" -ForegroundColor Red
        Write-Host "        Loi: $($_.Exception.Message)" -ForegroundColor Yellow
        $script:failCount++
    }
}

function Assert-Throws([scriptblock]$Block, [string]$ExpectedFragment) {
    $caught = $false
    $msg = ""
    try {
        & $Block | Out-Null
    } catch {
        $caught = $true
        $msg = $_.Exception.Message
    }
    if (-not $caught) { throw "Mong doi nem loi chua '$ExpectedFragment' nhung khong co loi nao duoc nem ra." }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedFragment) -and $msg -notmatch [regex]::Escape($ExpectedFragment)) {
        throw "Loi nhan duoc khong chua '$ExpectedFragment'. Thuc te: $msg"
    }
}

# Nap module common + cac ham guard tu restore-drill.ps1 (khong chay luong chinh)
. $commonScript
. $restoreScript -TestOnlyValidation

# ==============================================================================
# NHOM 1: H1 — DENY-BY-DEFAULT HOST GUARD
# ==============================================================================
Write-Host "`n--- [NHOM 1] H1: HOST GUARD DENY-BY-DEFAULT ---"

$srcParams = @{ Host = "ep-xxx.neon.tech"; Port = 5432; Database = "neondb" }

Run-GuardTest 'H1.1: Host la (db-prod.internal) khong co trong allowlist -> NEM LOI chan' {
    Assert-Throws { Assert-SafeDrillEnvironment -TargetParams @{ Host = "db-prod.internal"; Port = 5432 } -SourceParams $srcParams -TargetDbName "eventpulse_drill_test1" } "KHONG nam trong Allowlist"
}

Run-GuardTest 'H1.2: Host localhost -> CHO PHEP (deny-by-default van cho localhost)' {
    Assert-SafeDrillEnvironment -TargetParams @{ Host = "localhost"; Port = 5432 } -SourceParams $srcParams -TargetDbName "eventpulse_drill_test2" | Out-Null
}

Run-GuardTest 'H1.3: Host 127.0.0.1 -> CHO PHEP' {
    Assert-SafeDrillEnvironment -TargetParams @{ Host = "127.0.0.1"; Port = 5432 } -SourceParams $srcParams -TargetDbName "eventpulse_drill_test3" | Out-Null
}

Run-GuardTest 'H1.4: Host container drill (s55drill_pg) chi duoc phep khi trong -AllowedHosts' {
    Assert-SafeDrillEnvironment -TargetParams @{ Host = "s55drill_pg"; Port = 5432 } -SourceParams $srcParams -TargetDbName "eventpulse_drill_test4" -AllowedHosts @("s55drill_pg") | Out-Null
}

Run-GuardTest 'H1.5: Host container drill KHONG co allowlist -> NEM LOI' {
    Assert-Throws { Assert-SafeDrillEnvironment -TargetParams @{ Host = "s55drill_pg"; Port = 5432 } -SourceParams $srcParams -TargetDbName "eventpulse_drill_test5" } "KHONG nam trong Allowlist"
}

Run-GuardTest 'H1.6: Host cloud (neon.tech) BI CHAN tuyet doi ngay ca khi trong allowlist' {
    Assert-Throws { Assert-SafeDrillEnvironment -TargetParams @{ Host = "ep-xxx.ap-southeast-1.aws.neon.tech"; Port = 5432 } -SourceParams $srcParams -TargetDbName "eventpulse_drill_test6" -AllowedHosts @("ep-xxx.ap-southeast-1.aws.neon.tech") } "ha tang Cloud"
}

Run-GuardTest 'H1.7: Ten database khong co tien to eventpulse_drill_ -> NEM LOI' {
    Assert-Throws { Assert-SafeDrillEnvironment -TargetParams @{ Host = "localhost"; Port = 5432 } -SourceParams $srcParams -TargetDbName "event_ticket_db" } "eventpulse_drill_"
}

Run-GuardTest 'H1.8: Dich trung nguon (cung host + cung database) -> NEM LOI' {
    $sameSource = @{ Host = "localhost"; Port = 5432; Database = "eventpulse_drill_x" }
    Assert-Throws { Assert-SafeDrillEnvironment -TargetParams @{ Host = "localhost"; Port = 5432 } -SourceParams $sameSource -TargetDbName "eventpulse_drill_x" } "trung hoan toan"
}

Run-GuardTest 'H1.9: TargetParams null -> NEM LOI fail-closed' {
    Assert-Throws { Assert-SafeDrillEnvironment -TargetParams $null -SourceParams $srcParams -TargetDbName "eventpulse_drill_test9" } "Host"
}

Run-GuardTest 'H1.10: Bien moi truong DRILL_ALLOWED_HOSTS duoc cong nhan' {
    $oldEnv = $env:DRILL_ALLOWED_HOSTS
    try {
        $env:DRILL_ALLOWED_HOSTS = "s55drill_pg;another-drill-host"
        Assert-SafeDrillEnvironment -TargetParams @{ Host = "another-drill-host"; Port = 5432 } -SourceParams $srcParams -TargetDbName "eventpulse_drill_test10" | Out-Null
    } finally {
        if ($null -ne $oldEnv) { $env:DRILL_ALLOWED_HOSTS = $oldEnv } else { Remove-Item env:DRILL_ALLOWED_HOSTS -ErrorAction SilentlyContinue }
    }
}

# ==============================================================================
# NHOM 2: H2 — XAC MINH MANIFEST + SHA-256 TRUOC KHI RESTORE
# ==============================================================================
Write-Host "`n--- [NHOM 2] H2: ARCHIVE INTEGRITY GUARD ---"

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_guard_test_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    # 1. Cap dump + manifest HOP LE
    $validDump = Join-Path $tempDir "eventpulse_backup_20261018_010000.dump"
    $validManifest = Join-Path $tempDir "eventpulse_backup_20261018_010000.json"
    [System.IO.File]::WriteAllBytes($validDump, [System.Text.Encoding]::UTF8.GetBytes("DUMMY_BACKUP_" + ("Y" * 3000)))
    $validHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $validDump).Hash
    @{ backupFile = "eventpulse_backup_20261018_010000.dump"; sizeBytes = 3012; sha256 = $validHash; status = "VerifiedValid"; sourceHost = "localhost"; sourceDatabase = "event_ticket_db" } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $validManifest -Encoding utf8

    Run-GuardTest 'H2.1: Dump + manifest hop le (hash khop) -> CHO PHEP' {
        $m = Assert-BackupArchiveIntegrity -DumpFilePath $validDump
        if ($m.status -ne "VerifiedValid") { throw "Manifest tra ve sai." }
    }

    Run-GuardTest 'H2.2: Test-S55BackupValidity tra IsValid=$true cho cap hop le' {
        $v = Test-S55BackupValidity -DumpPath $validDump
        if (-not $v.IsValid -or $v.Reason -ne "Valid") { throw "Ket qua khong dung: $($v.Reason)" }
    }

    # 2. Thieu manifest
    $noManifestDump = Join-Path $tempDir "eventpulse_backup_20261018_020000.dump"
    [System.IO.File]::WriteAllBytes($noManifestDump, [System.Text.Encoding]::UTF8.GetBytes("DUMMY_BACKUP_" + ("Z" * 3000)))
    Run-GuardTest 'H2.3: Dump thieu manifest -> TU CHOI' {
        Assert-Throws { Assert-BackupArchiveIntegrity -DumpFilePath $noManifestDump } "manifest"
    }
    Run-GuardTest 'H2.4: Test-S55BackupValidity tra IsValid=$false (ManifestNotFound)' {
        $v = Test-S55BackupValidity -DumpPath $noManifestDump
        if ($v.IsValid -or $v.Reason -ne "ManifestNotFound") { throw "Ket qua khong dung: $($v.Reason)" }
    }

    # 3. Sai SHA-256
    $badHashDump = Join-Path $tempDir "eventpulse_backup_20261018_030000.dump"
    $badHashManifest = Join-Path $tempDir "eventpulse_backup_20261018_030000.json"
    [System.IO.File]::WriteAllBytes($badHashDump, [System.Text.Encoding]::UTF8.GetBytes("DUMMY_BACKUP_" + ("Q" * 3000)))
    @{ backupFile = "eventpulse_backup_20261018_030000.dump"; sizeBytes = 3012; sha256 = ("0" * 64); status = "VerifiedValid" } |
        ConvertTo-Json | Set-Content -LiteralPath $badHashManifest -Encoding utf8
    Run-GuardTest 'H2.5: SHA-256 khong khop -> TU CHOI' {
        Assert-Throws { Assert-BackupArchiveIntegrity -DumpFilePath $badHashDump } "KHONG KHOP"
    }
    Run-GuardTest 'H2.6: Test-S55BackupValidity tra IsValid=$false (Sha256Mismatch)' {
        $v = Test-S55BackupValidity -DumpPath $badHashDump
        if ($v.IsValid -or $v.Reason -ne "Sha256Mismatch") { throw "Ket qua khong dung: $($v.Reason)" }
    }

    # 4. Status khong phai VerifiedValid
    $badStatusDump = Join-Path $tempDir "eventpulse_backup_20261018_040000.dump"
    $badStatusManifest = Join-Path $tempDir "eventpulse_backup_20261018_040000.json"
    [System.IO.File]::WriteAllBytes($badStatusDump, [System.Text.Encoding]::UTF8.GetBytes("DUMMY_BACKUP_" + ("W" * 3000)))
    $badStatusHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $badStatusDump).Hash
    @{ backupFile = "eventpulse_backup_20261018_040000.dump"; sizeBytes = 3012; sha256 = $badStatusHash; status = "InProgress" } |
        ConvertTo-Json | Set-Content -LiteralPath $badStatusManifest -Encoding utf8
    Run-GuardTest 'H2.7: Manifest status khong VerifiedValid -> TU CHOI' {
        Assert-Throws { Assert-BackupArchiveIntegrity -DumpFilePath $badStatusDump } "VerifiedValid"
    }

    # 5. Ten file khong khop manifest
    $nameMismatchDump = Join-Path $tempDir "eventpulse_backup_20261018_050000.dump"
    $nameMismatchManifest = Join-Path $tempDir "eventpulse_backup_20261018_050000.json"
    [System.IO.File]::WriteAllBytes($nameMismatchDump, [System.Text.Encoding]::UTF8.GetBytes("DUMMY_BACKUP_" + ("R" * 3000)))
    $nmHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $nameMismatchDump).Hash
    @{ backupFile = "eventpulse_backup_20261018_999999.dump"; sizeBytes = 3012; sha256 = $nmHash; status = "VerifiedValid" } |
        ConvertTo-Json | Set-Content -LiteralPath $nameMismatchManifest -Encoding utf8
    Run-GuardTest 'H2.8: Ten file trong manifest khong khop -> TU CHOI' {
        Assert-Throws { Assert-BackupArchiveIntegrity -DumpFilePath $nameMismatchDump } "khong khop"
    }

    # 6. Dump qua nho
    $smallDump = Join-Path $tempDir "eventpulse_backup_20261018_060000.dump"
    $smallManifest = Join-Path $tempDir "eventpulse_backup_20261018_060000.json"
    [System.IO.File]::WriteAllBytes($smallDump, [System.Text.Encoding]::UTF8.GetBytes("TINY"))
    @{ backupFile = "eventpulse_backup_20261018_060000.dump"; sizeBytes = 4; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $smallDump).Hash; status = "VerifiedValid" } |
        ConvertTo-Json | Set-Content -LiteralPath $smallManifest -Encoding utf8
    Run-GuardTest 'H2.9: Dump <= 1024 bytes -> TU CHOI' {
        Assert-Throws { Assert-BackupArchiveIntegrity -DumpFilePath $smallDump } "qua nho"
    }

    # 7. Manifest JSON hong
    $badJsonDump = Join-Path $tempDir "eventpulse_backup_20261018_070000.dump"
    $badJsonManifest = Join-Path $tempDir "eventpulse_backup_20261018_070000.json"
    [System.IO.File]::WriteAllBytes($badJsonDump, [System.Text.Encoding]::UTF8.GetBytes("DUMMY_BACKUP_" + ("T" * 3000)))
    Set-Content -LiteralPath $badJsonManifest -Value "{ khong phai json hop le" -Encoding utf8
    Run-GuardTest 'H2.10: Manifest JSON khong doc duoc -> TU CHOI' {
        Assert-Throws { Assert-BackupArchiveIntegrity -DumpFilePath $badJsonDump } "manifest"
    }

} finally {
    if (Test-Path -LiteralPath $tempDir) {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ==============================================================================
# NHOM 3: H3 — TIMEOUT AN TOAN CHO TIEN TRINH
# ==============================================================================
Write-Host "`n--- [NHOM 3] H3: PROCESS TIMEOUT GUARD ---"

$enginePath = (Get-Process -Id $PID).Path
if ([string]::IsNullOrWhiteSpace($enginePath)) { $enginePath = "pwsh" }

Run-GuardTest 'H3.1: Lenh nhanh exit 0 -> Finished=true, ExitCode=0' {
    $r = Invoke-ProcessWithTimeout -FilePath $enginePath -Arguments @("-NoProfile", "-Command", "exit 0") -TimeoutSeconds 60
    if (-not $r.Finished -or $r.TimedOut -or $r.ExitCode -ne 0) { throw "Ket qua khong dung: $($r | ConvertTo-Json -Compress)" }
}

Run-GuardTest 'H3.2: Lenh nhanh exit 3 -> ExitCode=3 (khong mat ma thoat)' {
    $r = Invoke-ProcessWithTimeout -FilePath $enginePath -Arguments @("-NoProfile", "-Command", "exit 3") -TimeoutSeconds 60
    if ($r.TimedOut -or $r.ExitCode -ne 3) { throw "Ket qua khong dung: $($r | ConvertTo-Json -Compress)" }
}

Run-GuardTest 'H3.3: Lenh treo (sleep 60) voi timeout 2s -> TimedOut=true, ExitCode=124' {
    $r = Invoke-ProcessWithTimeout -FilePath $enginePath -Arguments @("-NoProfile", "-Command", "Start-Sleep -Seconds 60") -TimeoutSeconds 2
    if (-not $r.TimedOut -or $r.Finished -or $r.ExitCode -ne 124) { throw "Ket qua khong dung: $($r | ConvertTo-Json -Compress)" }
}

Run-GuardTest 'H3.4: Lenh treo da bi terminate — tien trinh khong con ton tai' {
    $pidFile = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_h3_pid_" + [Guid]::NewGuid().ToString("N") + ".txt")
    try {
        $r = Invoke-ProcessWithTimeout -FilePath $enginePath -Arguments @("-NoProfile", "-Command", "Set-Content '$pidFile' -Value `$PID; Start-Sleep -Seconds 120") -TimeoutSeconds 2
        Start-Sleep -Milliseconds 500
        $childPid = [int](Get-Content -LiteralPath $pidFile -Raw -ErrorAction SilentlyContinue)
        if ($childPid -gt 0) {
            $alive = Get-Process -Id $childPid -ErrorAction SilentlyContinue
            if ($null -ne $alive) { throw "Tien trinh con dang song sau khi timeout (PID $childPid)!" }
        }
    } finally {
        if (Test-Path -LiteralPath $pidFile) { Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue }
    }
}

# ==============================================================================
# TONG KET
# ==============================================================================
Write-Host "`n=================================================================="
Write-Host " TONG KET KIEM THU GUARD H1/H2/H3"
Write-Host " - So test PASS : $passCount" -ForegroundColor Green
Write-Host " - So test FAIL : $failCount" -ForegroundColor $(if ($failCount -eq 0) { "Green" } else { "Red" })
Write-Host "=================================================================="

if ($failCount -gt 0) {
    exit 1
}
exit 0
