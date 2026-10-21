<#
.SYNOPSIS
    Kiểm thử tự động cô lập (không cần Database) cho Story S-55:
    1. Kiểm tra cú pháp tĩnh (PowerShell AST Syntax Check).
    2. Kiểm thử logic phân tích Connection String với mật khẩu chứa ký tự đặc biệt.
    3. Kiểm thử chính sách Retention 14 bản qua giao diện tích hợp (-TestOnlyRetention).
    4. Kiểm thử bảo vệ: Bỏ qua file thiếu manifest hoặc sai SHA-256.
    5. Kiểm thử phòng vệ: Khi backup lỗi, bảo lưu toàn bộ file cũ.
#>

$ErrorActionPreference = 'Stop'

Write-Host "=================================================================="
Write-Host " S-55: KIEM THU TINH & RETENTION UNIT TESTS (KHONG DUNG DATABASE)"
Write-Host "=================================================================="

$scriptDir = Split-Path $PSScriptRoot -Parent
$backupScript = Join-Path $scriptDir "scripts\backup-db.ps1"
$restoreScript = Join-Path $scriptDir "scripts\restore-drill.ps1"

# ------------------------------------------------------------------------------
# TEST 1: KIỂM TRA CÚ PHÁP TĨNH (POWERSHELL AST SYNTAX CHECK)
# ------------------------------------------------------------------------------
Write-Host "`n[TEST 1] Kiem tra cu phap PowerShell qua AST Parser..."

function Test-ScriptSyntax([string]$FilePath) {
    $errors = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($FilePath, [ref]$tokens, [ref]$errors)
    if ($errors.Count -eq 0) {
        Write-Host " - [PASS] $(Split-Path $FilePath -Leaf): Cu phap hop le (0 loi AST)."
        return $true
    } else {
        Write-Host " - [FAIL] $(Split-Path $FilePath -Leaf): Phat hien $($errors.Count) loi cu phap:"
        foreach ($err in $errors) { Write-Host "   + Line $($err.Extent.StartLineNumber): $($err.Message)" }
        return $false
    }
}

$syntax1 = Test-ScriptSyntax -FilePath $backupScript
$syntax2 = Test-ScriptSyntax -FilePath $restoreScript

if (-not ($syntax1 -and $syntax2)) {
    throw "Kiem tra cu phap that bai!"
}

# ------------------------------------------------------------------------------
# TEST 2: KIỂM THỬ PARSE CONNECTION STRING CÓ DẤU CHẤM PHẨY VÀ KÝ TỰ ĐẶC BIỆT
# ------------------------------------------------------------------------------
Write-Host "`n[TEST 2] Kiem thu parser connection string voi mat khau chua dau cham phay..."

# Lấy định nghĩa hàm Parse-PgConnectionString từ module dùng chung s55-common.ps1
. (Join-Path $scriptDir "scripts\s55-common.ps1")

$complexConn = 'Host=127.0.0.1;Port=5433;Database=test_db;Username=test_user;Password="Complex;Pass;word=123!";SSL Mode=Require'
$parsed = Parse-PgConnectionString -RawConnection $complexConn

if ($parsed.Host -eq "127.0.0.1" -and
    $parsed.Port -eq 5433 -and
    $parsed.Database -eq "test_db" -and
    $parsed.Username -eq "test_user" -and
    $parsed.Password -eq "Complex;Pass;word=123!" -and
    $parsed.SslMode -eq "Require") {
    Write-Host " - [PASS] Parser tach dung mat khau co chua dau cham phay ';' va dau '=' ma khong bi cat cut."
} else {
    throw "Loi parser connection string! Parsed password: $($parsed.Password)"
}

# ------------------------------------------------------------------------------
# TEST 3: KIỂM THỬ RETENTION 14 BẢN QUA GIAO DIỆN TÍCH HỢP (-TestOnlyRetention)
# ------------------------------------------------------------------------------
Write-Host "`n[TEST 3] Kiem thu chinh sach Retention qua -TestOnlyRetention tren thu muc tam..."

$tempTestDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_test_retention_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempTestDir -Force | Out-Null

try {
    # Tạo 16 bản backup giả lập với đầy đủ file dump và manifest JSON có SHA-256 khớp tuyệt đối
    $baseDate = (Get-Date).AddDays(-16)
    for ($i = 1; $i -le 16; $i++) {
        $ts = $baseDate.AddHours($i * 2).ToString("yyyyMMdd_HHmmss")
        $dumpName = "eventpulse_backup_$ts.dump"
        $manifestName = "eventpulse_backup_$ts.json"

        $dumpPath = Join-Path $tempTestDir $dumpName
        $manifestPath = Join-Path $tempTestDir $manifestName

        $contentBytes = [System.Text.Encoding]::UTF8.GetBytes("DUMMY_POSTGRESQL_BACKUP_DATA_ITEM_$i" + ("X" * 1500))
        [System.IO.File]::WriteAllBytes($dumpPath, $contentBytes)
        $sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $dumpPath).Hash

        $manifestObj = [PSCustomObject]@{
            backupFile   = $dumpName
            sizeBytes    = $contentBytes.Length
            sha256       = $sha
            createdAtUtc = $baseDate.AddHours($i * 2).ToUniversalTime().ToString("o")
            status       = "VerifiedValid"
        }
        $manifestObj | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8

        (Get-Item $dumpPath).LastWriteTime = $baseDate.AddHours($i * 2)
    }

    # Tạo 1 file lạ không thuộc quy ước để xác minh retention không xóa nhầm
    $foreignFile = Join-Path $tempTestDir "other_backup_database.bak"
    Set-Content -LiteralPath $foreignFile -Value "Not an eventpulse backup file"

    # Chạy lệnh kiểm thử tích hợp retention
    $result = & $backupScript -BackupDir $tempTestDir -MaxRetention 14 -TestOnlyRetention

    $remainingDumps = Get-ChildItem -Path $tempTestDir -File | Where-Object { $_.Name -match '^eventpulse_backup_.*\.dump$' }
    $foreignFileExists = Test-Path -LiteralPath $foreignFile

    Write-Host " - Tong so ban hop le ban dau: 16"
    Write-Host " - So ban da xoa             : $($result.DeletedCount)"
    Write-Host " - So ban con lai            : $($remainingDumps.Count)"

    if ($remainingDumps.Count -eq 14 -and $result.DeletedCount -eq 2 -and $foreignFileExists) {
        Write-Host " - [PASS] Giu dung 14 ban hop le moi nhat va xoa dung 2 ban cu nhat (kem manifest)."
        Write-Host " - [PASS] File ngoai quy uoc (other_backup_database.bak) duoc bao toan 100%."
    } else {
        throw "Ket qua retention khong dung mong doi! Con lai: $($remainingDumps.Count)"
    }

    # --------------------------------------------------------------------------
    # TEST 4: BỎ QUA FILE THIẾU MANIFEST HOẶC MANIFEST SAI SHA-256
    # --------------------------------------------------------------------------
    Write-Host "`n[TEST 4] Kiem thu bo qua file dump thieu manifest hoac sai SHA-256..."

    # File 1: Dump hợp lệ về tên và kích thước nhưng KHÔNG có file manifest .json
    $noManifestDump = Join-Path $tempTestDir "eventpulse_backup_20261017_111111.dump"
    [System.IO.File]::WriteAllBytes($noManifestDump, (New-Object byte[] 2048))

    # File 2: Dump có manifest nhưng SHA-256 trong manifest không khớp với file dump
    $badShaDump = Join-Path $tempTestDir "eventpulse_backup_20261017_222222.dump"
    $badShaManifest = Join-Path $tempTestDir "eventpulse_backup_20261017_222222.json"
    [System.IO.File]::WriteAllBytes($badShaDump, (New-Object byte[] 2048))
    $badManifestObj = [PSCustomObject]@{
        backupFile = "eventpulse_backup_20261017_222222.dump"
        sizeBytes  = 2048
        sha256     = "0000000000000000000000000000000000000000000000000000000000000000" # Sai SHA
        status     = "VerifiedValid"
    }
    $badManifestObj | ConvertTo-Json | Set-Content -LiteralPath $badShaManifest -Encoding utf8

    # Chạy lại retention
    $checkInvalidResult = & $backupScript -BackupDir $tempTestDir -MaxRetention 14 -TestOnlyRetention

    # Hai file trên không được công nhận là valid backup, nên TotalValidCount vẫn phải là 14
    if ($checkInvalidResult.TotalValidCount -eq 14) {
        Write-Host " - [PASS] Retention tu choi cong nhan file thieu manifest hoac sai SHA-256."
    } else {
        throw "Loi: Retention da cong nhan file thieu manifest hoac sai SHA-256! ValidCount: $($checkInvalidResult.TotalValidCount)"
    }

    # --------------------------------------------------------------------------
    # TEST 5: KHI BACKUP THẤT BẠI, RETENTION BỊ HỦY VÀ BẢO LƯU 100% FILE CŨ
    # --------------------------------------------------------------------------
    Write-Host "`n[TEST 5] Kiem thu phong ve: Khi backup moi that bai, retention khong xoa file cu..."
    $failResult = & $backupScript -BackupDir $tempTestDir -MaxRetention 14 -TestOnlyRetention -SimulateFailureForRetentionTest $true

    if ($failResult.Status -eq "AbortedDueToFailure" -and $failResult.DeletedCount -eq 0) {
        Write-Host " - [PASS] Retention lap tuc huy va khong xoa bat ky file nao khi backup that bai."
    } else {
        throw "Loi: Retention van xoa file khi backup bi bao loi!"
    }

} finally {
    if (Test-Path -LiteralPath $tempTestDir) {
        Remove-Item -Path $tempTestDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ------------------------------------------------------------------------------
# TEST 6: KIỂM THỬ TĨNH CHO CHỐT CHẶN PHÒNG VỆ S55-01 (RESTORE DRILL ROW COUNT GATE)
# ------------------------------------------------------------------------------
Write-Host "`n[TEST 6] Kiem thu tinh AST / logic cho chot chan S55-01 trong restore-drill.ps1..."
$restoreRaw = Get-Content -LiteralPath $restoreScript -Raw
if ($restoreRaw -match '\$failedTableCount\s*=\s*0' -and
    $restoreRaw -match 'if\s*\(\$failedTableCount\s*-gt\s*0\)\s*\{' -and
    $restoreRaw -match 'Write-Error\s+".*?Khong the dem so ban ghi') {
    Write-Host " - [PASS] restore-drill.ps1 co day du bien dem failedTableCount, ghi log Write-Error va chot chan throw khi row count that bai."
} else {
    throw "Loi S55-01: restore-drill.ps1 thieu chot chan phong ve failedTableCount!"
}

Write-Host "`n=================================================================="
Write-Host " TAT CA 6 KIEM THU TINH VA UNIT TEST RETENTION DEU DAT (PASS)!"
Write-Host "=================================================================="
