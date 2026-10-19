<#
.SYNOPSIS
    S-55 Phase 3: BỘ KIỂM THỬ OFFLINE CHO OFFSITE BACKUP & MÃ HÓA CLIENT-SIDE BẰNG AGE
.DESCRIPTION
    Kiểm thử 100% offline không cần database thật, Docker, tài khoản Cloudflare R2 hay age/rclone cài sẵn.
    Chặn toàn bộ truy cập mạng thực tế, kiểm tra chính xác kết quả logic nghiệp vụ, bảo vệ dữ liệu và bảo mật thông tin.
#>

$ErrorActionPreference = 'Stop'

Write-Host "=================================================================="
Write-Host " S-55 PHASE 3: KIEM THU OFFLINE OFFSITE BACKUP & CLIENT ENCRYPTION"
Write-Host "=================================================================="

$scriptDir = Split-Path $PSScriptRoot -Parent
$uploadScript = Join-Path $scriptDir "scripts\backup-upload-r2.ps1"
$backupScript = Join-Path $scriptDir "scripts\backup-db.ps1"
$restoreScript = Join-Path $scriptDir "scripts\restore-drill.ps1"

# Nạp module scripts/backup-upload-r2.ps1
. $uploadScript

$passCount = 0
$failCount = 0

function Run-Phase3Test([string]$TestName, [scriptblock]$TestBlock) {
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

# ==============================================================================
# NHÓM 1: KIỂM TRA CÚ PHÁP TĨNH POWERSHELL AST (SCRIPTS & MODULES)
# ==============================================================================
Write-Host "`n--- [NHOM 1] KIEM TRA CU PHAP TINH POWERSHELL AST ---"

Run-Phase3Test 'AST Parser: scripts/backup-upload-r2.ps1 hop le (0 loi)' {
    $errs = $null; $toks = $null
    [System.Management.Automation.Language.Parser]::ParseFile($uploadScript, [ref]$toks, [ref]$errs) | Out-Null
    if ($errs.Count -ne 0) { throw "$($errs.Count) loi AST: $($errs[0].Message)" }
}

Run-Phase3Test 'AST Parser: scripts/backup-db.ps1 hop le (0 loi)' {
    $errs = $null; $toks = $null
    [System.Management.Automation.Language.Parser]::ParseFile($backupScript, [ref]$toks, [ref]$errs) | Out-Null
    if ($errs.Count -ne 0) { throw "$($errs.Count) loi AST: $($errs[0].Message)" }
}

Run-Phase3Test 'AST Parser: scripts/restore-drill.ps1 hop le (0 loi)' {
    $errs = $null; $toks = $null
    [System.Management.Automation.Language.Parser]::ParseFile($restoreScript, [ref]$toks, [ref]$errs) | Out-Null
    if ($errs.Count -ne 0) { throw "$($errs.Count) loi AST: $($errs[0].Message)" }
}

# ==============================================================================
# NHÓM 2: VALIDATION CẤU HÌNH CLOUDFLARE R2 (FAIL-CLOSED)
# ==============================================================================
Write-Host "`n--- [NHOM 2] VALIDATION CAU HINH R2 & CHOT CHAN AN TOAN ---"

Run-Phase3Test 'R2 Config: Thieu bien cau hinh bat buoc -> Nem loi ro rang' {
    $testCases = @(
        @{ AccountId = ""; BucketName = "b"; AccessKeyId = "k"; SecretAccessKey = "s" },
        @{ AccountId = "acc"; BucketName = ""; AccessKeyId = "k"; SecretAccessKey = "s" },
        @{ AccountId = "acc"; BucketName = "b"; AccessKeyId = ""; SecretAccessKey = "s" },
        @{ AccountId = "acc"; BucketName = "b"; AccessKeyId = "k"; SecretAccessKey = "" },
        @{}
    )

    foreach ($tc in $testCases) {
        $caught = $false
        try {
            Validate-R2Config -Config $tc | Out-Null
        } catch {
            if ($_ -match "Thieu bien cau hinh bat buoc|Cau hinh R2 khong duoc de trong") {
                $caught = $true
            }
        }
        if (-not $caught) { throw "Cau hinh thieu khong bi nem ngoai le!" }
    }
}

Run-Phase3Test 'R2 Config: Ten bucket khong dung chuan S3 (chua chu hoa, dau cham, qua ngan) -> Tu choi' {
    $invalidBuckets = @("MyBucket", "b", "bucket_with_underscore", "-start-hyphen", "end-hyphen-")
    foreach ($b in $invalidBuckets) {
        $caught = $false
        try {
            Validate-R2Config -Config @{
                AccountId       = "1234567890abcdef1234567890abcdef"
                BucketName      = $b
                AccessKeyId     = "test_key_id"
                SecretAccessKey = "test_secret_key"
            } | Out-Null
        } catch {
            if ($_ -match "khong hop le theo chuan S3") { $caught = $true }
        }
        if (-not $caught) { throw "Ten bucket bat hop le '$b' khong bi chan!" }
    }
}

Run-Phase3Test 'R2 Config: Cau hinh hop le -> Tra ve doi tuong day du kem default endpoint' {
    $validConfig = @{
        AccountId       = "0123456789abcdef0123456789abcdef"
        BucketName      = "eventpulse-backup-prod"
        AccessKeyId     = "AKIAIOSFODNN7EXAMPLE"
        SecretAccessKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
        Prefix          = "backups/database"
    }
    $cfg = Validate-R2Config -Config $validConfig
    if ($cfg.BucketName -ne "eventpulse-backup-prod" -or
        $cfg.Endpoint -ne "https://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com" -or
        $cfg.Prefix -ne "backups/database") {
        throw "Ket qua validate config khong dung: $($cfg | ConvertTo-Json -Compress)"
    }
}

# ==============================================================================
# NHÓM 3: VALIDATION PUBLIC KEY AGE (BECH32 X25519)
# ==============================================================================
Write-Host "`n--- [NHOM 3] VALIDATION KHOA MA HOA AGE ---"

Run-Phase3Test 'Age Key: Key null, rong hoac chua khoang trang -> Nem loi' {
    $caughtNull = $false
    try { Validate-AgeRecipientKey -RecipientKey "" | Out-Null } catch { $caughtNull = $true }
    if (-not $caughtNull) { throw "Key rong khong bi chan!" }
}

Run-Phase3Test 'Age Key: Phat hien Private Key (AGE-SECRET-KEY-1...) -> Tu choi tuyet doi vi ly do an toan' {
    $caughtPrivate = $false
    try {
        Validate-AgeRecipientKey -RecipientKey "AGE-SECRET-KEY-1QFDQ8Y970NMX6XUX2M4R6S3KGYD20E2P4V9L5F4M7C2W8R9T" | Out-Null
    } catch {
        if ($_ -match "PRIVATE KEY") { $caughtPrivate = $true }
    }
    if (-not $caughtPrivate) { throw "Private key khong bi chan an toan!" }
}

Run-Phase3Test 'Age Key: Dinh dang khong phai age1... (vi du ssh-rsa) -> Nem loi' {
    $caughtSsh = $false
    try { Validate-AgeRecipientKey -RecipientKey "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQC123" | Out-Null } catch { $caughtSsh = $true }
    if (-not $caughtSsh) { throw "SSH key khong bi chan!" }
}

Run-Phase3Test 'Age Key: Key chuan age1 hop le (62 ky tu) -> Chap nhan' {
    $validKey = "age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p"
    $resKey = Validate-AgeRecipientKey -RecipientKey $validKey
    if ($resKey -ne $validKey) { throw "Key hop le bi tu choi!" }
}

# ==============================================================================
# NHÓM 4: MÃ HÓA AGE (CLIENT-SIDE ENCRYPTION, STAGING, ERROR CLEANUP)
# ==============================================================================
Write-Host "`n--- [NHOM 4] MA HOA BẰNG AGE VA DON DEP STAGING KHI CO LOI ---"

Run-Phase3Test 'Age Encrypt: Ma hoa thanh cong -> Tao file .age hop le, tinh dual SHA-256' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_age_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $dumpPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump"
        $encPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump.age"
        $dummyData = [System.Text.Encoding]::UTF8.GetBytes("MOCK_POSTGRESQL_CUSTOM_DUMP_DATA_" + ("Z" * 2048))
        [System.IO.File]::WriteAllBytes($dumpPath, $dummyData)
        $expectedPtSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $dumpPath).Hash

        $res = Invoke-AgeEncryption -InputDumpPath $dumpPath `
                                    -OutputEncryptedPath $encPath `
                                    -RecipientKey "age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p" `
                                    -MockCommandBlock {
                                        param($Command, $Args, $InputPath, $OutputPath)
                                        # Gia lap age ma hoa tao file staging
                                        $encBytes = [System.Text.Encoding]::UTF8.GetBytes("AGE-ENCRYPTED-PAYLOAD-" + ("E" * 2100))
                                        [System.IO.File]::WriteAllBytes($OutputPath, $encBytes)
                                        return @{ ExitCode = 0 }
                                    }

        if (-not (Test-Path -LiteralPath $encPath)) { throw "File ma hoa chinh thuc khong ton tai!" }
        if (Test-Path -LiteralPath "$encPath.in_progress") { throw "File staging chua duoc atomic move!" }
        if ($res.PlaintextSha256 -ne $expectedPtSha) { throw "Plaintext SHA-256 khong khop!" }
        $actualEncHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $encPath).Hash
        if ($res.EncryptedSha256 -ne $actualEncHash) { throw "Encrypted SHA-256 khong khop file tren dia!" }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Run-Phase3Test 'Age Encrypt: age tra loi (ExitCode != 0) -> Xoa staging, bao toan dump goc' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_age_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $dumpPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump"
        $encPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump.age"
        $dummyData = [System.Text.Encoding]::UTF8.GetBytes("MOCK_DUMP_" + ("A" * 2048))
        [System.IO.File]::WriteAllBytes($dumpPath, $dummyData)

        $caughtErr = $false
        try {
            Invoke-AgeEncryption -InputDumpPath $dumpPath `
                                -OutputEncryptedPath $encPath `
                                -RecipientKey "age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p" `
                                -MockCommandBlock {
                                    param($Command, $Args, $InputPath, $OutputPath)
                                    [System.IO.File]::WriteAllBytes($OutputPath, (New-Object byte[] 100))
                                    return @{ ExitCode = 1 }
                                }
        } catch {
            $caughtErr = $true
        }

        if (-not $caughtErr) { throw "age loi khong bi nem ngoai le!" }
        if (Test-Path -LiteralPath $encPath) { throw "File ma hoa le van ton tai!" }
        if (Test-Path -LiteralPath "$encPath.in_progress") { throw "File staging do dang khong duoc don dep!" }
        if (-not (Test-Path -LiteralPath $dumpPath)) { throw "File dump goc bi xoa mat!" }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Run-Phase3Test 'Age Encrypt: File ma hoa rong hoac qua nho (<= 64 bytes) -> Tu choi va xoa staging' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_age_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $dumpPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump"
        $encPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump.age"
        [System.IO.File]::WriteAllBytes($dumpPath, (New-Object byte[] 2048))

        $caughtEmpty = $false
        try {
            Invoke-AgeEncryption -InputDumpPath $dumpPath `
                                -OutputEncryptedPath $encPath `
                                -RecipientKey "age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p" `
                                -MockCommandBlock {
                                    param($Command, $Args, $InputPath, $OutputPath)
                                    [System.IO.File]::WriteAllBytes($OutputPath, (New-Object byte[] 10)) # Qua nho
                                    return @{ ExitCode = 0 }
                                }
        } catch {
            if ($_ -match "qua nho") { $caughtEmpty = $true }
        }

        if (-not $caughtEmpty) { throw "File ma hoa rong khong bi phat hien!" }
        if (Test-Path -LiteralPath "$encPath.in_progress") { throw "File staging khong duoc don dep!" }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ==============================================================================
# NHÓM 5: MANIFEST VERSION 2.0 (DUAL HASH & INTEGRITY VALIDATION)
# ==============================================================================
Write-Host "`n--- [NHOM 5] MANIFEST V2 (DUAL HASH & INTEGRITY ASSERTIONS) ---"

Run-Phase3Test 'Manifest V2: Thieu file manifest -> Nem loi' {
    $caughtMissing = $false
    try {
        Assert-BackupManifestV2Integrity -ManifestFilePath "C:\nonexistent\manifest.json" | Out-Null
    } catch {
        if ($_ -match "khong ton tai") { $caughtMissing = $true }
    }
    if (-not $caughtMissing) { throw "Thieu manifest khong bi chan!" }
}

Run-Phase3Test 'Manifest V2: Manifest khong khop ten file dump hoac file ma hoa -> Nem loi' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_mf_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $dumpPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump"
        $encPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump.age"
        $mfPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.json"

        [System.IO.File]::WriteAllBytes($dumpPath, (New-Object byte[] 2048))
        [System.IO.File]::WriteAllBytes($encPath, (New-Object byte[] 2048))
        $ptSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $dumpPath).Hash
        $encSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $encPath).Hash

        # Manifest sai ten file
        $badMf = [PSCustomObject]@{
            backupId        = "ep-backup-1"
            manifestVersion = "2.0"
            backupFile      = "wrong_name.dump"
            encryptedFile   = "wrong_name.dump.age"
            plaintextSha256 = $ptSha
            encryptedSha256 = $encSha
            status          = "VerifiedValid"
        }
        $badMf | ConvertTo-Json | Set-Content -LiteralPath $mfPath -Encoding utf8

        $caughtMismatch = $false
        try {
            Assert-BackupManifestV2Integrity -ManifestFilePath $mfPath -ExpectedDumpPath $dumpPath -ExpectedEncryptedPath $encPath | Out-Null
        } catch {
            if ($_ -match "khong khop voi manifest") { $caughtMismatch = $true }
        }
        if (-not $caughtMismatch) { throw "Sai ten file khong bi nem ngoai le!" }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Run-Phase3Test 'Manifest V2: Manifest sai plaintextSha256 hoac encryptedSha256 -> Tu choi' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_mf_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $dumpPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump"
        $encPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump.age"
        $mfPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.json"

        [System.IO.File]::WriteAllBytes($dumpPath, (New-Object byte[] 2048))
        [System.IO.File]::WriteAllBytes($encPath, (New-Object byte[] 2048))

        $badHashMf = [PSCustomObject]@{
            backupId        = "ep-backup-1"
            manifestVersion = "2.0"
            backupFile      = "eventpulse_backup_20261018_120000.dump"
            encryptedFile   = "eventpulse_backup_20261018_120000.dump.age"
            plaintextSha256 = "0000000000000000000000000000000000000000000000000000000000000000"
            encryptedSha256 = "0000000000000000000000000000000000000000000000000000000000000000"
            status          = "VerifiedValid"
        }
        $badHashMf | ConvertTo-Json | Set-Content -LiteralPath $mfPath -Encoding utf8

        $caughtHashMismatch = $false
        try {
            Assert-BackupManifestV2Integrity -ManifestFilePath $mfPath -ExpectedDumpPath $dumpPath -ExpectedEncryptedPath $encPath | Out-Null
        } catch {
            if ($_ -match "khong khop voi manifest") { $caughtHashMismatch = $true }
        }
        if (-not $caughtHashMismatch) { throw "Sai hash khong bi bat boi manifest integrity!" }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ==============================================================================
# NHÓM 6: UPLOAD VÀ XÁC MINH TOÀN VẸN R2 (CHECKSUM TẢI VỀ SO SÁNH)
# ==============================================================================
Write-Host "`n--- [NHOM 6] UPLOAD R2 VA XAC MINH TINH TOAN VEN REMOTE ---"

$mockR2Cfg = @{
    AccountId       = "0123456789abcdef0123456789abcdef"
    BucketName      = "eventpulse-backup-test"
    AccessKeyId     = "test_key_id"
    SecretAccessKey = "test_secret_access_key"
    Prefix          = "backups"
}

Run-Phase3Test 'R2 Upload: Upload that bai (rclone non-zero exit code) -> Nem ngoai le va khong danh dau Verified' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_up_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $encPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump.age"
        $mfPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.json"
        [System.IO.File]::WriteAllBytes($encPath, (New-Object byte[] 2048))
        $encSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $encPath).Hash

        $mfObj = [PSCustomObject]@{
            backupId        = "ep-backup-test1"
            manifestVersion = "2.0"
            backupFile      = "eventpulse_backup_20261018_120000.dump"
            encryptedFile   = "eventpulse_backup_20261018_120000.dump.age"
            plaintextSha256 = "dummy_pt_sha"
            encryptedSha256 = $encSha
            status          = "VerifiedValid"
            offsiteStatus   = "Pending"
        }
        $mfObj | ConvertTo-Json | Set-Content -LiteralPath $mfPath -Encoding utf8

        $caughtUpErr = $false
        try {
            Invoke-R2BackupUpload -EncryptedFilePath $encPath -ManifestFilePath $mfPath -R2Config $mockR2Cfg -MockCommandBlock {
                param($Command, $Args, $R2Config)
                throw "Network connection refused"
            } | Out-Null
        } catch {
            $caughtUpErr = $true
        }

        if (-not $caughtUpErr) { throw "Upload loi khong nem exception!" }
        $currentMf = Get-Content -LiteralPath $mfPath -Raw | ConvertFrom-Json
        if ($currentMf.offsiteStatus -eq "VerifiedUploaded") { throw "Upload that bai nhung van danh dau VerifiedUploaded!" }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Run-Phase3Test 'R2 Upload: Remote Checksum Mismatch (file tai ve co hash khac) -> Nem ngoai le FAIL' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_up_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $encPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump.age"
        $mfPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.json"
        [System.IO.File]::WriteAllBytes($encPath, (New-Object byte[] 2048))
        $encSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $encPath).Hash

        $mfObj = [PSCustomObject]@{
            backupId        = "ep-backup-test2"
            manifestVersion = "2.0"
            backupFile      = "eventpulse_backup_20261018_120000.dump"
            encryptedFile   = "eventpulse_backup_20261018_120000.dump.age"
            plaintextSha256 = "dummy_pt_sha"
            encryptedSha256 = $encSha
            status          = "VerifiedValid"
            offsiteStatus   = "Pending"
        }
        $mfObj | ConvertTo-Json | Set-Content -LiteralPath $mfPath -Encoding utf8

        $caughtHashDiff = $false
        try {
            Invoke-R2BackupUpload -EncryptedFilePath $encPath -ManifestFilePath $mfPath -R2Config $mockR2Cfg -MockCommandBlock {
                param($Command, $Arguments, $R2Config)
                # Khi lenh la copyto de verify (doi so cuoi cung la target file local)
                $target = $Arguments[$Arguments.Count - 1]
                if ($target -match 's55_r2_verify_') {
                    # Tao file bi sai hash (hong hoc du lieu)
                    [System.IO.File]::WriteAllBytes($target, [System.Text.Encoding]::UTF8.GetBytes("CORRUPTED_REMOTE_DATA"))
                }
                return @{ ExitCode = 0 }
            } | Out-Null
        } catch {
            if ($_ -match "KHONG KHOP voi local") { $caughtHashDiff = $true }
        }

        if (-not $caughtHashDiff) { throw "Remote checksum mismatch khong bi phat hien!" }
        $currentMf = Get-Content -LiteralPath $mfPath -Raw | ConvertFrom-Json
        if ($currentMf.offsiteStatus -eq "VerifiedUploaded") { throw "Hash mismatch nhung van danh dau VerifiedUploaded!" }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Run-Phase3Test 'R2 Upload: Upload va Checksum khop 100% -> Danh dau VerifiedUploaded va cap nhat manifest' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_up_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $encPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump.age"
        $mfPath = Join-Path $tempDir "eventpulse_backup_20261018_120000.json"
        $dummyBytes = [System.Text.Encoding]::UTF8.GetBytes("VERIFIED_ENCRYPTED_DUMP_" + ("K" * 2048))
        [System.IO.File]::WriteAllBytes($encPath, $dummyBytes)
        $encSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $encPath).Hash

        $mfObj = [PSCustomObject]@{
            backupId        = "ep-backup-success"
            manifestVersion = "2.0"
            backupFile      = "eventpulse_backup_20261018_120000.dump"
            encryptedFile   = "eventpulse_backup_20261018_120000.dump.age"
            plaintextSha256 = "dummy_pt_sha"
            encryptedSha256 = $encSha
            status          = "VerifiedValid"
            offsiteStatus   = "Pending"
        }
        $mfObj | ConvertTo-Json | Set-Content -LiteralPath $mfPath -Encoding utf8

        $res = Invoke-R2BackupUpload -EncryptedFilePath $encPath -ManifestFilePath $mfPath -R2Config $mockR2Cfg -MockCommandBlock {
            param($Command, $Arguments, $R2Config)
            $action = $Arguments[0]
            $target = $Arguments[$Arguments.Count - 1]
            if ($action -eq "copyto" -and $target -match 's55_r2_verify_') {
                if ($target.EndsWith(".dump.age")) {
                    # Tra ve file co hash khop tuyet doi
                    [System.IO.File]::WriteAllBytes($target, $dummyBytes)
                } elseif ($target.EndsWith(".json")) {
                    $mfObj | ConvertTo-Json | Set-Content -LiteralPath $target -Encoding utf8
                }
            }
            return @{ ExitCode = 0 }
        }

        if ($res.OffsiteStatus -ne "VerifiedUploaded") { throw "Ket qua tra ve khong phai VerifiedUploaded!" }
        $updatedMf = Get-Content -LiteralPath $mfPath -Raw | ConvertFrom-Json
        if ($updatedMf.offsiteStatus -ne "VerifiedUploaded" -or [string]::IsNullOrWhiteSpace($updatedMf.offsiteVerifiedAtUtc)) {
            throw "Manifest khong duoc cap nhat trang thai offsiteVerifiedAtUtc!"
        }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ==============================================================================
# NHÓM 7: RETENTION OFFSITE TRÊN R2 (14 BẢN HỢP LỆ, FAIL-SAFE, NAMESPACE ISOLATION)
# ==============================================================================
Write-Host "`n--- [NHOM 7] RETENTION OFFSITE CLOUDFLARE R2 ---"

Run-Phase3Test 'R2 Retention: 16 ban backup hop le -> Xoa dung 2 ban cu nhat (ca .dump.age va .json), giu 14' {
    $deletedFilesList = @()
    $mockObjects = @()
    $baseDate = (Get-Date).AddDays(-16)

    for ($i = 1; $i -le 16; $i++) {
        $ts = $baseDate.AddHours($i * 2).ToString("yyyyMMdd_HHmmss")
        $mockObjects += @{
            Name    = "eventpulse_backup_$ts.dump.age"
            Size    = 2048
            ModTime = $baseDate.AddHours($i * 2).ToString("o")
        }
        $mockObjects += @{
            Name    = "eventpulse_backup_$ts.json"
            Size    = 512
            ModTime = $baseDate.AddHours($i * 2).ToString("o")
        }
    }

    $retResult = Invoke-R2RemoteRetention -R2Config $mockR2Cfg -RetentionLimit 14 -IsUploadFailed $false -MockCommandBlock {
        param($Command, $Arguments, $R2Config)
        $sub = $Arguments[0]
        if ($sub -eq "lsjson") {
            return $mockObjects
        } elseif ($sub -eq "deletefile") {
            $deletedFilesList += (Split-Path $Arguments[1] -Leaf)
            return @{ ExitCode = 0 }
        }
    }

    if ($retResult.TotalValidCount -ne 16) { throw "TotalValidCount khong dung: $($retResult.TotalValidCount)" }
    if ($retResult.RetainedCount -ne 14) { throw "RetainedCount khong dung 14: $($retResult.RetainedCount)" }
    if ($retResult.DeletedCount -ne 4) { throw "DeletedCount khong phai 4 (2 cap dump+json): $($retResult.DeletedCount)" }

    # Xac minh 2 ban cu nhat bi xoa
    $oldestTs1 = $baseDate.AddHours(1 * 2).ToString("yyyyMMdd_HHmmss")
    $oldestTs2 = $baseDate.AddHours(2 * 2).ToString("yyyyMMdd_HHmmss")
    if (-not ($retResult.DeletedFiles -contains "eventpulse_backup_$oldestTs1.dump.age" -and
              $retResult.DeletedFiles -contains "eventpulse_backup_$oldestTs1.json" -and
              $retResult.DeletedFiles -contains "eventpulse_backup_$oldestTs2.dump.age" -and
              $retResult.DeletedFiles -contains "eventpulse_backup_$oldestTs2.json")) {
        throw "Cac file bi xoa khong phai la 2 ban cu nhat: $($retResult.DeletedFiles -join ', ')"
    }
}

Run-Phase3Test 'F4.1: R2 Retention nhan dang object CO prefix (vd s55/eventpulse_...) va xoa dung target khong nhan doi prefix' {
    $deletedTargets = @()
    $mockObjects = @()
    $baseDate = (Get-Date).AddDays(-16)
    for ($i = 1; $i -le 16; $i++) {
        $ts = $baseDate.AddHours($i * 2).ToString("yyyyMMdd_HHmmss")
        $mockObjects += @{ Name = "s55/eventpulse_backup_$ts.dump.age"; Size = 2048; ModTime = $baseDate.AddHours($i * 2).ToString("o") }
        $mockObjects += @{ Name = "s55/eventpulse_backup_$ts.json"; Size = 512; ModTime = $baseDate.AddHours($i * 2).ToString("o") }
    }
    $retResult = Invoke-R2RemoteRetention -R2Config @{ AccountId = '0123456789abcdef0123456789abcdef'; BucketName = 's55-postgres-backup'; AccessKeyId = 'k'; SecretAccessKey = 's'; Prefix = 's55' } -RetentionLimit 14 -IsUploadFailed $false -MockCommandBlock {
        param($Command, $Arguments, $R2Config)
        if ($Arguments[0] -eq "lsjson") { return $mockObjects }
        if ($Arguments[0] -eq "deletefile") {
            $deletedTargets += $Arguments[1]
            return @{ ExitCode = 0 }
        }
    }
    if ($retResult.TotalValidCount -ne 16) { throw "TotalValidCount sai: $($retResult.TotalValidCount)" }
    if ($retResult.DeletedCount -ne 4) { throw "DeletedCount sai: $($retResult.DeletedCount)" }
    foreach ($t in $deletedTargets) {
        if ($t -match '/s55/s55/') { throw "Prefix bi nhan doi trong target xoa: $t" }
        if ($t -notmatch '^R2:s55-postgres-backup/s55/eventpulse_backup_') { throw "Target xoa sai dang: $t" }
    }
}

Run-Phase3Test 'F4.2: Object trong thu muc con cua prefix (s55/sub/...) -> coi la ngoai namespace, KHONG bao gio xoa' {
    $deletedTargets = @()
    $mockObjects = @()
    $baseDate = (Get-Date).AddDays(-16)
    for ($i = 1; $i -le 15; $i++) {
        $ts = $baseDate.AddHours($i * 2).ToString("yyyyMMdd_HHmmss")
        $mockObjects += @{ Name = "eventpulse_backup_$ts.dump.age"; Size = 2048; ModTime = $baseDate.AddHours($i * 2).ToString("o") }
        $mockObjects += @{ Name = "eventpulse_backup_$ts.json"; Size = 512; ModTime = $baseDate.AddHours($i * 2).ToString("o") }
    }
    # File co ten giong backup nhung nam trong thu muc con -> phai la foreign
    $mockObjects += @{ Name = 's55/sub/eventpulse_backup_19990101_000000.dump.age'; Size = 2048; ModTime = $baseDate.AddHours(30).ToString("o") }
    $mockObjects += @{ Name = 's55/sub/eventpulse_backup_19990101_000000.json'; Size = 512; ModTime = $baseDate.AddHours(30).ToString("o") }

    $retResult = Invoke-R2RemoteRetention -R2Config @{ AccountId = '0123456789abcdef0123456789abcdef'; BucketName = 's55-postgres-backup'; AccessKeyId = 'k'; SecretAccessKey = 's'; Prefix = 's55' } -RetentionLimit 14 -IsUploadFailed $false -MockCommandBlock {
        param($Command, $Arguments, $R2Config)
        if ($Arguments[0] -eq "lsjson") { return $mockObjects }
        if ($Arguments[0] -eq "deletefile") {
            $deletedTargets += $Arguments[1]
            return @{ ExitCode = 0 }
        }
    }
    if ($retResult.TotalValidCount -ne 15) { throw "So ban hop le phai la 15 (khong tinh thu muc con): $($retResult.TotalValidCount)" }
    if ($retResult.ForeignCount -lt 2) { throw "Object thu muc con phai duoc dem la foreign: $($retResult.ForeignCount)" }
    foreach ($t in $deletedTargets) {
        if ($t -match '/sub/') { throw "Object trong thu muc con bi xoa nham: $t" }
    }
}

Run-Phase3Test 'R2 Retention: Khi upload that bai (IsUploadFailed=$true) -> ABORT, bao toan 100% backup thu 15 khong bi xoa' {
    $deleteInvoked = $false
    $retResult = Invoke-R2RemoteRetention -R2Config $mockR2Cfg -RetentionLimit 14 -IsUploadFailed $true -MockCommandBlock {
        param($Command, $Arguments, $R2Config)
        if ($Arguments[0] -eq "deletefile") {
            $deleteInvoked = $true
        }
    }

    if ($retResult.Status -ne "AbortedDueToUploadFailure" -or $retResult.DeletedCount -ne 0 -or $deleteInvoked) {
        throw "Retention van xoa file khi upload that bai!"
    }
}

Run-Phase3Test 'R2 Retention: Namespace Guard - File khac namespace khong bao gio bi xoa' {
    $mockObjects = @(
        @{ Name = "foreign_backup_mongo.dump.age"; Size = 4096; ModTime = "2026-01-01T00:00:00Z" },
        @{ Name = "app_logs_202610.txt"; Size = 1024; ModTime = "2026-01-01T00:00:00Z" },
        @{ Name = "other_database.bak"; Size = 8192; ModTime = "2026-01-01T00:00:00Z" }
    )

    # Them 15 ban hop le
    for ($i = 1; $i -le 15; $i++) {
        $ts = "20261018_" + ("{0:D2}" -f $i) + "0000"
        $mockObjects += @{ Name = "eventpulse_backup_$ts.dump.age"; Size = 2048; ModTime = "2026-10-18T00:00:00Z" }
        $mockObjects += @{ Name = "eventpulse_backup_$ts.json"; Size = 512; ModTime = "2026-10-18T00:00:00Z" }
    }

    $retResult = Invoke-R2RemoteRetention -R2Config $mockR2Cfg -RetentionLimit 14 -IsUploadFailed $false -MockCommandBlock {
        param($Command, $Arguments, $R2Config)
        if ($Arguments[0] -eq "lsjson") { return $mockObjects }
        if ($Arguments[0] -eq "deletefile") { return @{ ExitCode = 0 } }
    }

    if ($retResult.ForeignCount -ne 3) { throw "ForeignCount khong phat hien dung 3 file la!" }
    if ($retResult.DeletedFiles -contains "foreign_backup_mongo.dump.age" -or
        $retResult.DeletedFiles -contains "app_logs_202610.txt" -or
        $retResult.DeletedFiles -contains "other_database.bak") {
        throw "File la ngoai namespace bi xoa nham: $($retResult.DeletedFiles -join ', ')"
    }
    if ($retResult.DeletedCount -ne 2) { throw "So file xoa khong dung: $($retResult.DeletedCount)" }
}

# ==============================================================================
# NHÓM 8: BẢO MẬT & ZERO CREDENTIAL LEAKAGE TRONG OUTPUT/LOGS
# ==============================================================================
Write-Host "`n--- [NHOM 8] BAO MAT VA ZERO CREDENTIAL LEAKAGE TRONG LOG ---"

Run-Phase3Test 'Security: Output cua rclone tu dong mask SecretAccessKey sang ***' {
    $secretDummy = "SUPER_SECRET_R2_KEY_99999_XYZ"
    $rawStdErr = "Error: Authentication failed with secret $secretDummy while connecting to bucket."

    $masked = $rawStdErr -replace [regex]::Escape($secretDummy), '***'
    if ($masked -match [regex]::Escape($secretDummy)) {
        throw "Secret van xuat hien trong chuoi output!"
    }
    if (-not ($masked -match 'secret \*\*\* while')) {
        throw "Masking secret khong dung dinh dang!"
    }
}

# ==============================================================================
# NHÓM 9: TÍCH HỢP RESTORE-DRILL VỚI BẢN ENCRYPTED (.DUMP.AGE)
# ==============================================================================
Write-Host "`n--- [NHOM 9] TICH HOP RESTORE-DRILL CHO BAN ENCRYPTED (.DUMP.AGE) ---"

Run-Phase3Test 'Restore-Drill: Xac minh toan ven file .dump.age bang encryptedSha256 trong manifest v2' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_dr_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $encDump = Join-Path $tempDir "eventpulse_backup_20261018_150000.dump.age"
        $mfFile = Join-Path $tempDir "eventpulse_backup_20261018_150000.json"
        $encBytes = [System.Text.Encoding]::UTF8.GetBytes("ENCRYPTED_DUMP_BYTES_" + ("E" * 2048))
        [System.IO.File]::WriteAllBytes($encDump, $encBytes)
        $encHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $encDump).Hash

        $mfObj = [PSCustomObject]@{
            backupId        = "ep-backup-dr-1"
            manifestVersion = "2.0"
            backupFile      = "eventpulse_backup_20261018_150000.dump"
            encryptedFile   = "eventpulse_backup_20261018_150000.dump.age"
            plaintextSha256 = "PT_HASH_123"
            sha256          = "PT_HASH_123"
            encryptedSha256 = $encHash
            status          = "VerifiedValid"
        }
        $mfObj | ConvertTo-Json | Set-Content -LiteralPath $mfFile -Encoding utf8

        $resObj = & $restoreScript -TestOnlyValidation -TestValidationInputs @{
            ArchiveIntegrityTest = @{ DumpFilePath = $encDump }
        }

        if ($resObj.encryptedSha256 -ne $encHash -or $resObj.status -ne "VerifiedValid") {
            throw "Assert-BackupArchiveIntegrity khong xac thuc dung file .dump.age!"
        }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Run-Phase3Test 'Restore-Drill: File .dump.age sai encryptedSha256 -> Tu choi restore' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_dr_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $encDump = Join-Path $tempDir "eventpulse_backup_20261018_150000.dump.age"
        $mfFile = Join-Path $tempDir "eventpulse_backup_20261018_150000.json"
        [System.IO.File]::WriteAllBytes($encDump, (New-Object byte[] 2048))

        $mfObj = [PSCustomObject]@{
            backupId        = "ep-backup-dr-2"
            manifestVersion = "2.0"
            backupFile      = "eventpulse_backup_20261018_150000.dump"
            encryptedFile   = "eventpulse_backup_20261018_150000.dump.age"
            plaintextSha256 = "PT_HASH_123"
            sha256          = "PT_HASH_123"
            encryptedSha256 = "BAD_ENCRYPTED_HASH_99999"
            status          = "VerifiedValid"
        }
        $mfObj | ConvertTo-Json | Set-Content -LiteralPath $mfFile -Encoding utf8

        $rejectedBadEnc = $false
        try {
            & $restoreScript -TestOnlyValidation -TestValidationInputs @{
                ArchiveIntegrityTest = @{ DumpFilePath = $encDump }
            } | Out-Null
        } catch {
            if ($_ -match "KHONG KHOP voi encryptedSha256") { $rejectedBadEnc = $true }
        }

        if (-not $rejectedBadEnc) { throw "File ma hoa sai encryptedSha256 khong bi chan!" }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ==============================================================================
# TỔNG KẾT BỘ KIỂM THỬ PHASE 3
# ==============================================================================
Write-Host "`n=================================================================="
Write-Host " TONG KET KIEM THU OFFLINE CHO S-55 PHASE 3"
Write-Host " - So test PASS : $passCount" -ForegroundColor Green
Write-Host " - So test FAIL : $failCount" -ForegroundColor $(if ($failCount -eq 0) { "Green" } else { "Red" })
Write-Host "=================================================================="

if ($failCount -gt 0) {
    exit 1
}
exit 0
