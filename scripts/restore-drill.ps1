<#
.SYNOPSIS
    S-55: Script diễn tập khôi phục (Restore Drill) vào cơ sở dữ liệu tạm thời và kiểm tra tính toàn vẹn đa cấp độ.

.DESCRIPTION
    - Chỉ cho phép khôi phục vào database tạm cô lập có tiền tố 'eventpulse_drill_'.
    - Cơ chế phòng vệ an toàn: Từ chối máy chủ Cloud/Production; từ chối nếu đích trùng với nguồn.
    - Tự động lấy thông tin nguồn từ manifest của bản backup nếu không truyền -SourceConnectionString.
    - Đóng dấu nhận diện quyền sở hữu (Drill Run ID) trước khi restore; chỉ drop database khi xác minh đúng quyền sở hữu.
    - An toàn khi lỗi: Nếu restore hoặc bất kỳ bước đối soát toàn vẹn nào thất bại, GIỮ NGUYÊN database để điều tra, tuyệt đối không DROP.
    - Không khẳng định dữ liệu toàn vẹn chỉ qua mã thoát của pg_restore: Kiểm tra động schema, migration history, bảng, foreign keys, unique indexes và row counts.
    - Hỗ trợ cờ -KeepDrillDb để giữ lại database phục vụ điều tra nếu cần.
    - Dọn dẹp biến môi trường nhạy cảm trong try/finally.
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$DumpFile,

    [Parameter()]
    [string]$BackupDir = "",

    [Parameter()]
    [string]$TargetConnectionString,

    [Parameter()]
    [string]$SourceConnectionString,

    [Parameter()]
    [string]$TargetDatabase,

    [Parameter()]
    [switch]$KeepDrillDb,

    [Parameter()]
    [string]$PgRestorePath,

    [Parameter()]
    [string]$PsqlPath,

    [Parameter()]
    [int]$MaxRtoSeconds = 900,

    [Parameter()]
    [int]$RestoreTimeoutSeconds = 1800,

    [Parameter()]
    [int]$VerifyTimeoutSeconds = 300,

    [Parameter()]
    [string]$ResultFile,

    [Parameter()]
    [string[]]$AllowedHosts = @(),

    [Parameter()]
    [string]$AgeIdentityFile,

    [Parameter()]
    [string]$AgeExePath,

    [Parameter()]
    [scriptblock]$AgeMockCommandBlock,

    [Parameter()]
    [switch]$TestOnlyValidation,

    [Parameter()]
    [hashtable]$TestValidationInputs
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot trong param default khong duoc nap dung gio trong PowerShell 5.1
# (advanced script), nen tinh duong dan o day de tuong thich ca 5.1 lan 7.x.
if ([string]::IsNullOrWhiteSpace($BackupDir)) {
    $BackupDir = Join-Path $PSScriptRoot "..\artifacts\database-backups"
}

# Nạp module dùng chung S-55 (Get-PgToolExecutable, Parse-PgConnectionString, Invoke-PgSqlQuery, Invoke-ProcessWithTimeout, Test-S55BackupValidity)
. (Join-Path $PSScriptRoot "s55-common.ps1")

# ==============================================================================
# HÀM BỔ TRỢ: KIỂM TRA NGƯỠNG RTO (<= 900 GIÂY)
# ==============================================================================
function Assert-RtoThreshold {
    param(
        [Parameter(Mandatory=$false)]
        $RtoSeconds,

        [Parameter()]
        [int]$MaxAllowedSeconds = 900
    )

    $parsedRto = 0.0
    if ($null -eq $RtoSeconds -or -not [double]::TryParse("$RtoSeconds", [ref]$parsedRto) -or $parsedRto -lt 0) {
        throw "[LOI RTO] Gia tri do luong RTO khong hop le hoac khong the xac dinh: '$RtoSeconds'."
    }

    $val = $parsedRto
    if ($val -gt $MaxAllowedSeconds) {
        throw "[RTO VUOT NGUONG] Thoi gian khoi phuc ($val giay) vuot qua nguong cho phep ($MaxAllowedSeconds giay). Dien tap danh gia THAT BAI."
    }

    return @{
        Passed            = $true
        RtoSeconds        = $val
        MaxAllowedSeconds = $MaxAllowedSeconds
        Status            = "RtoWithinThreshold"
    }
}

# ==============================================================================
# HÀM BỔ TRỢ: XỬ LÝ VÀ PHÂN LOẠI EXIT CODE PG_RESTORE
# ==============================================================================
function Assert-PgRestoreExitCode {
    param(
        [Parameter(Mandatory=$false)]
        $ExitCode
    )

    $code = 0
    if ($null -eq $ExitCode -or -not [int]::TryParse("$ExitCode", [ref]$code)) {
        throw "[LOI EXIT CODE] Ma thoat cua pg_restore khong hop le hoac khong xac dinh: '$ExitCode'."
    }
    if ($code -eq 0) {
        return @{
            Passed   = $true
            ExitCode = 0
            Status   = "CleanSuccess"
            Message  = "pg_restore hoan thanh sach se khong co canh bao hay loi."
        }
    } elseif ($code -eq 1) {
        Write-Warning "[RESTORE] pg_restore tra ve ma thoat 1 (co canh bao khong nghiem trong, vi du bo qua quyen so huu/privileges do --no-owner --no-privileges). Tien hanh doi soat toan ven nghiem ngat..."
        return @{
            Passed   = $true
            ExitCode = 1
            Status   = "WarningAccepted"
            Message  = "pg_restore hoan thanh voi canh bao chap nhan duoc (can doi soat toan ven)."
        }
    } else {
        throw "[LOI RESTORE] pg_restore that bai voi ma thoat nghiem trong: $code."
    }
}

# ==============================================================================
# HÀM BỔ TRỢ: KIỂM TRA ĐIỀU KIỆN TỰ ĐỘNG CHO SCHEMA / FK / INDEX
# ==============================================================================
function Assert-DatabaseIntegrityCounts {
    param(
        [Parameter()]
        $MigrationCount,

        [Parameter()]
        $TableCount,

        [Parameter()]
        $FkCount,

        [Parameter()]
        $IndexCount
    )

    $m = 0
    $t = 0
    $f = 0
    $idx = 0

    if ($null -eq $MigrationCount -or -not [int]::TryParse($MigrationCount.ToString(), [ref]$m) -or $m -le 0) {
        throw "Kiem tra that bai: Khong tim thay lich su migration nao trong __EFMigrationsHistory (Count: '$MigrationCount')."
    }

    if ($null -eq $TableCount -or -not [int]::TryParse($TableCount.ToString(), [ref]$t) -or $t -le 0) {
        throw "Kiem tra that bai: Database sau khi khoi phuc khong co bang nghiep vu nao (Count: '$TableCount')."
    }

    if ($null -eq $FkCount -or -not [int]::TryParse($FkCount.ToString(), [ref]$f) -or $f -le 0) {
        throw "Kiem tra that bai: Database sau khi khoi phuc khong co rang buoc Foreign Key nao (Count: '$FkCount')."
    }

    if ($null -eq $IndexCount -or -not [int]::TryParse($IndexCount.ToString(), [ref]$idx) -or $idx -le 0) {
        throw "Kiem tra that bai: Database sau khi khoi phuc khong co Index nao (Count: '$IndexCount')."
    }

    return @{
        Passed         = $true
        MigrationCount = $m
        TableCount     = $t
        FkCount        = $f
        IndexCount     = $idx
        Status         = "IntegrityPassed"
    }
}

# ==============================================================================
# HÀM BỔ TRỢ: ĐỐI CHIẾU BASELINE ROW COUNTS (MANIFEST LÚC BACKUP) VỚI DB ĐÃ RESTORE
# Lưu ý: baseline (backup-db.ps1) ghi nhận CẢ bảng tiền tố '__' (ví dụ __EFMigrationsHistory),
# trong khi danh sách thống kê sau restore chỉ đếm bảng nghiệp vụ (loại trừ '__').
# Bảng hệ thống đã được đếm riêng ở Cấp 1 (MigrationCount) nên được bổ sung vào bản đồ đối chiếu.
# ==============================================================================
function Compare-S55BaselineRowCounts {
    param(
        [Parameter(Mandatory)]
        $Baseline,

        [Parameter(Mandatory)]
        [array]$TableStats,

        [Parameter()]
        [long]$MigrationCount = 0
    )

    $mismatches = @()
    $statsByName = @{}
    foreach ($s in $TableStats) {
        if ($null -ne $s) { $statsByName[[string]$s.TableName] = [long]$s.RowCount }
    }

    # S55-BASELINE-FIX: baseline ghi lúc backup bao gồm cả bảng hệ thống '__EFMigrationsHistory'.
    # Bảng này bị loại khỏi $TableStats (chỉ đếm bảng nghiệp vụ), nên bổ sung giá trị đã đếm ở Cấp 1
    # để tránh kết luận sai "THIEU BANG" khi bảng thực tế đã được khôi phục đầy đủ.
    if ($Baseline.PSObject.Properties.Name -contains '__EFMigrationsHistory' -and -not $statsByName.ContainsKey('__EFMigrationsHistory')) {
        $statsByName['__EFMigrationsHistory'] = $MigrationCount
    }

    foreach ($prop in $Baseline.PSObject.Properties) {
        $tbl = $prop.Name
        $expected = [long]$prop.Value
        if ($statsByName.ContainsKey($tbl)) {
            if ($statsByName[$tbl] -ne $expected) {
                $mismatches += "$tbl (baseline: $expected, restored: $($statsByName[$tbl]))"
            }
        } else {
            $mismatches += "$tbl (baseline: $expected, restored: THIEU BANG)"
        }
    }

    # Bảng có trong DB khôi phục nhưng không có trong baseline: cảnh báo (không fail) — có thể là bảng mới thêm sau lần backup
    foreach ($t in $statsByName.Keys) {
        if (-not ($Baseline.PSObject.Properties.Name -contains $t)) {
            Write-Warning "[BASELINE] Bang '$t' co trong DB khoi phuc nhung khong co trong baseline manifest (co the la bang moi them sau khi backup)."
        }
    }

    return @{ Compared = $true; MismatchCount = $mismatches.Count; Mismatches = $mismatches }
}

# ==============================================================================
# HÀM BỔ TRỢ: XÁC MINH MANIFEST VÀ SHA-256 CỦA BẢN BACKUP (H2 GUARD)
# ==============================================================================
function Assert-BackupArchiveIntegrity {
    param(
        [Parameter(Mandatory)]
        [string]$DumpFilePath
    )

    if (-not (Test-Path -LiteralPath $DumpFilePath)) {
        throw "[LOI TOAN VEN BACKUP] File dump khong ton tai: $DumpFilePath"
    }

    $dumpItem = Get-Item -LiteralPath $DumpFilePath
    if ($dumpItem.Length -le 1024) {
        throw "[LOI TOAN VEN BACKUP] File dump co dung luong qua nho ($($dumpItem.Length) bytes), nghi ngo dump loi hoac rong."
    }

    $manifestPath = [System.IO.Path]::ChangeExtension($DumpFilePath, ".json")
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        # Nếu là file .dump.age, thử tìm file manifest .json bỏ đuôi .age
        $altManifest = $DumpFilePath -replace '\.age$', ''
        $altManifest = [System.IO.Path]::ChangeExtension($altManifest, ".json")
        if (Test-Path -LiteralPath $altManifest) {
            $manifestPath = $altManifest
        } else {
            throw "[LOI TOAN VEN BACKUP] Khong tim thay manifest JSON di kem cho file dump: $($dumpItem.Name). Tu choi restore."
        }
    }

    $manifestContent = $null
    try {
        $manifestContent = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    } catch {
        throw "[LOI TOAN VEN BACKUP] Khong the doc hoac parse manifest JSON cua file: $($dumpItem.Name)."
    }

    if ($manifestContent.status -ne "VerifiedValid") {
        throw "[LOI TOAN VEN BACKUP] Trang thai manifest khong phai 'VerifiedValid' (Hien tai: '$($manifestContent.status)'). Tu choi restore."
    }

    $isEncrypted = $dumpItem.Name.EndsWith(".age")
    if ($isEncrypted) {
        if ([string]::IsNullOrWhiteSpace($manifestContent.encryptedFile)) {
            throw "[LOI TOAN VEN BACKUP] File ma hoa ($($dumpItem.Name)) can manifest v2 co truong 'encryptedFile'."
        }
        if ($manifestContent.encryptedFile -ne $dumpItem.Name) {
            throw "[LOI TOAN VEN BACKUP] Ten file ma hoa trong manifest ('$($manifestContent.encryptedFile)') khong khop voi ten file ('$($dumpItem.Name)')."
        }
        if ([string]::IsNullOrWhiteSpace($manifestContent.encryptedSha256)) {
            throw "[LOI TOAN VEN BACKUP] Manifest khong chua ma bam encryptedSha256 cho file ma hoa."
        }
        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $DumpFilePath).Hash
        if ($actualHash -ne $manifestContent.encryptedSha256) {
            throw "[LOI TOAN VEN BACKUP] Ma bam SHA-256 thuc te ($actualHash) KHONG KHOP voi encryptedSha256 trong manifest ($($manifestContent.encryptedSha256)). File ma hoa bi loi truyen dan."
        }
    } else {
        if ($manifestContent.backupFile -ne $dumpItem.Name) {
            throw "[LOI TOAN VEN BACKUP] Ten file trong manifest ('$($manifestContent.backupFile)') khong khop voi ten file dump ('$($dumpItem.Name)')."
        }
        $expectedSha = if ($manifestContent.PSObject.Properties['plaintextSha256']) { $manifestContent.plaintextSha256 } else { $manifestContent.sha256 }
        if ([string]::IsNullOrWhiteSpace($expectedSha)) {
            throw "[LOI TOAN VEN BACKUP] Manifest khong chua ma bam SHA-256."
        }
        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $DumpFilePath).Hash
        if ($actualHash -ne $expectedSha) {
            throw "[LOI TOAN VEN BACKUP] Ma bam SHA-256 thuc te ($actualHash) KHONG KHOP voi SHA-256 trong manifest ($expectedSha). File dump co dau hieu bi sua doi hoac loi truyen dan."
        }
    }

    return $manifestContent
}

# ==============================================================================
# HÀM BỔ TRỢ: KIỂM TRA MÔI TRƯỜNG AN TOÀN (CHỐNG GHI ĐÈ PRODUCTION/CLOUD - H1 GUARD)
# ==============================================================================
function Assert-SafeDrillEnvironment {
    param(
        [hashtable]$TargetParams,
        [hashtable]$SourceParams,
        [string]$TargetDbName,
        [string[]]$AllowedHosts = @()
    )

    # 1. Deny-by-default Host Guard: Chi cho phep localhost hoac host duoc khai bao tuong minh trong allowlist
    if ($null -eq $TargetParams -or [string]::IsNullOrWhiteSpace($TargetParams.Host)) {
        throw "[PHONG VE AN TOAN 1 - DENY BY DEFAULT] Khong xac dinh duoc Host may chu dich trong chuoi ket noi."
    }

    $targetHost = $TargetParams.Host.ToLower().Trim()
    # DENY-BY-DEFAULT: Chỉ localhost được phép mặc định. Mọi host khác (kể cả container drill) phải được khai báo tường minh.
    $defaultAllowed = @("localhost", "127.0.0.1", "::1")
    $customAllowed = @()
    if ($null -ne $AllowedHosts -and $AllowedHosts.Count -gt 0) {
        $customAllowed += ($AllowedHosts | ForEach-Object { $_.ToLower().Trim() })
    }
    if (-not [string]::IsNullOrWhiteSpace($env:DRILL_ALLOWED_HOSTS)) {
        $customAllowed += ($env:DRILL_ALLOWED_HOSTS -split '[,;]' | ForEach-Object { $_.ToLower().Trim() })
    }
    $effectiveAllowed = @($defaultAllowed + $customAllowed | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)

    # Tu choi tuyet doi cac domain cloud/production (defense-in-depth)
    $forbiddenCloudDomains = @("neon.tech", "render.com", "amazonaws.com", "supabase.co", "azure.com", "google.com")
    foreach ($domain in $forbiddenCloudDomains) {
        if ($targetHost.Contains($domain)) {
            throw "[PHONG VE AN TOAN 1 - DENY BY DEFAULT] May chu dich thuoc ha tang Cloud ($domain). Khong duoc phep dien tap tren Cloud/Production."
        }
    }

    # Bat buoc host phai nam trong Allowlist (Deny-by-default)
    if ($effectiveAllowed -notcontains $targetHost) {
        throw "[PHONG VE AN TOAN 1 - DENY BY DEFAULT] May chu dich '$targetHost' bi TU CHOI vi khong nam trong Allowlist duoc phep ($($effectiveAllowed -join ', ')). Host la bi chan truoc bat ky thao tac CSDL nao."
    }

    # 2. Kiem tra tien to bat buoc cua Database tam (Khong coi tien to la bien phap duy nhat)
    if (-not $TargetDbName.StartsWith("eventpulse_drill_")) {
        throw "[PHONG VE AN TOAN 2] Ten database dich phai bat dau bang tien to 'eventpulse_drill_'."
    }

    # 3. Kiem tra xung dot voi nguon backup
    if (-not [string]::IsNullOrWhiteSpace($SourceParams.Host) -and -not [string]::IsNullOrWhiteSpace($SourceParams.Database)) {
        if ($TargetParams.Host -eq $SourceParams.Host -and $TargetDbName -eq $SourceParams.Database) {
            throw "[PHONG VE AN TOAN 3] Database dich trung hoan toan voi Database nguon! Khong dien tap de len nguon."
        }
    }
}

# ==============================================================================
# NẾU CHỈ KIỂM THỬ VALIDATION (CHẾ ĐỘ TEST CÔ LẬP KHÔNG DÙNG DATABASE)
# ==============================================================================
if ($TestOnlyValidation) {
    Write-Host "[TEST MODE] Dang chay kiem thu co lap cac chot chan validation..."
    if ($null -ne $TestValidationInputs) {
        if ($TestValidationInputs.ContainsKey("HostSafetyTest")) {
            $inputTarget = $TestValidationInputs.HostSafetyTest.TargetParams
            $inputSource = $TestValidationInputs.HostSafetyTest.SourceParams
            $inputDbName = $TestValidationInputs.HostSafetyTest.TargetDbName
            $inputAllowed = if ($TestValidationInputs.HostSafetyTest.ContainsKey("AllowedHosts")) { $TestValidationInputs.HostSafetyTest.AllowedHosts } else { $AllowedHosts }
            return Assert-SafeDrillEnvironment -TargetParams $inputTarget -SourceParams $inputSource -TargetDbName $inputDbName -AllowedHosts $inputAllowed
        }
        if ($TestValidationInputs.ContainsKey("ArchiveIntegrityTest")) {
            return Assert-BackupArchiveIntegrity -DumpFilePath $TestValidationInputs.ArchiveIntegrityTest.DumpFilePath
        }
        if ($TestValidationInputs.ContainsKey("RtoTest")) {
            $allowedSec = $MaxRtoSeconds
            if ($TestValidationInputs.RtoTest.ContainsKey("MaxAllowedSeconds") -and $null -ne $TestValidationInputs.RtoTest.MaxAllowedSeconds) {
                $allowedSec = [int]$TestValidationInputs.RtoTest.MaxAllowedSeconds
            }
            return Assert-RtoThreshold -RtoSeconds $TestValidationInputs.RtoTest.RtoSeconds -MaxAllowedSeconds $allowedSec
        }
        if ($TestValidationInputs.ContainsKey("ExitCodeTest")) {
            return Assert-PgRestoreExitCode -ExitCode $TestValidationInputs.ExitCodeTest.ExitCode
        }
        if ($TestValidationInputs.ContainsKey("IntegrityTest")) {
            return Assert-DatabaseIntegrityCounts -MigrationCount $TestValidationInputs.IntegrityTest.MigrationCount `
                                                 -TableCount $TestValidationInputs.IntegrityTest.TableCount `
                                                 -FkCount $TestValidationInputs.IntegrityTest.FkCount `
                                                 -IndexCount $TestValidationInputs.IntegrityTest.IndexCount
        }
    }
    return @{ Status = "TestModeReady" }
}

# ==============================================================================
# KHỞI CHẠY QUY TRÌNH DIỄN TẬP
# ==============================================================================
Write-Host "=================================================================="
Write-Host " S-55: DIEN TAP KHOI PHUC CO SO DU LIEU (DISASTER RECOVERY DRILL)"
Write-Host "=================================================================="

$startTime = Get-Date

# 1. Kiểm tra công cụ pg_restore và psql
$resolvedPgRestore = Get-PgToolExecutable -ToolName "pg_restore" -ExplicitPath $PgRestorePath
$resolvedPsql = Get-PgToolExecutable -ToolName "psql" -ExplicitPath $PsqlPath

if (-not $resolvedPgRestore) {
    Write-Error "[LOI MOI TRUONG] Khong tim thay 'pg_restore'. Vui long kiem tra PATH hoac -PgRestorePath."
    exit 1
}

if (-not $resolvedPsql) {
    Write-Error "[LOI MOI TRUONG] Khong tim thay 'psql'. Vui long kiem tra PATH hoac -PsqlPath."
    exit 1
}

# 2. Xác định file dump cần khôi phục & Xác minh Manifest + SHA-256 (H2 Guard)
$selectedManifest = $null
if ([string]::IsNullOrWhiteSpace($DumpFile)) {
    Write-Host "[INPUT] Khong chi dinh file dump cu the, dang tim ban backup hop le moi nhat trong: $BackupDir"
    if (-not (Test-Path -LiteralPath $BackupDir)) {
        Write-Error "[LOI DU LIEU] Thu muc backup khong ton tai: $BackupDir"
        exit 1
    }

    $candidateFiles = Get-ChildItem -Path $BackupDir -File |
        Where-Object { $_.Name -match '^eventpulse_backup_\d{8}_\d{6}\.dump(\.age)?$' } |
        Sort-Object -Property LastWriteTime -Descending

    if ($candidateFiles.Count -eq 0) {
        Write-Error "[LOI DU LIEU] Khong tim thay file backup dang 'eventpulse_backup_*.dump' trong: $BackupDir"
        exit 1
    }

    # Lọc chỉ chọn bản backup có manifest hợp lệ và SHA-256 khớp tuyệt đối
    $verifiedCandidate = $null
    foreach ($cand in $candidateFiles) {
        try {
            $manifestObj = Assert-BackupArchiveIntegrity -DumpFilePath $cand.FullName
            $verifiedCandidate = $cand
            $selectedManifest = $manifestObj
            break
        } catch {
            Write-Warning "[LOI TOAN VEN BACKUP] Bo qua file dump khong hop le $($cand.Name): $($_.Exception.Message)"
        }
    }

    if ($null -eq $verifiedCandidate) {
        Write-Error "[LOI TOAN VEN BACKUP] Khong tim thay ban backup nao co manifest hop le va SHA-256 khop trong: $BackupDir"
        exit 1
    }

    $DumpFile = $verifiedCandidate.FullName
    $selectedDumpName = Split-Path $DumpFile -Leaf
    Write-Host "[INPUT] Da chon ban backup hop le moi nhat: $selectedDumpName (SHA-256: $($selectedManifest.sha256))"
} else {
    if (-not (Test-Path -LiteralPath $DumpFile)) {
        Write-Error "[LOI DU LIEU] File dump chi dinh khong ton tai: $DumpFile"
        exit 1
    }
    # Xác minh manifest và SHA-256 của file dump chỉ định
    $selectedManifest = Assert-BackupArchiveIntegrity -DumpFilePath $DumpFile
    Write-Host "[INPUT] File dump chi dinh hop le (SHA-256: $($selectedManifest.sha256))"
}

# 2.1 Nếu backup được mã hóa (.dump.age) thì giải mã về thư mục tạm trước khi restore
$restoreDumpFile = $DumpFile
$tempPlaintextDump = $null
$decryptTempDir = $null
if ($DumpFile.EndsWith(".age")) {
    $identityFile = $AgeIdentityFile
    if ([string]::IsNullOrWhiteSpace($identityFile) -and -not [string]::IsNullOrWhiteSpace($env:AGE_IDENTITY_FILE)) {
        $identityFile = $env:AGE_IDENTITY_FILE
    }
    if ([string]::IsNullOrWhiteSpace($identityFile)) {
        Write-Error "[LOI GIAI MA] Ban backup ma hoa (.dump.age) can identity file. Truyen -AgeIdentityFile hoac dat bien moi truong AGE_IDENTITY_FILE."
        exit 1
    }
    $uploadModulePath = Join-Path $PSScriptRoot "backup-upload-r2.ps1"
    if (-not (Test-Path -LiteralPath $uploadModulePath)) {
        Write-Error "[LOI GIAI MA] Khong tim thay scripts/backup-upload-r2.ps1 (chua ham Invoke-AgeDecryption)."
        exit 1
    }
    . $uploadModulePath

    $decryptTempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_drill_decrypt_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $decryptTempDir -Force | Out-Null
    $tempPlaintextDump = Join-Path $decryptTempDir ([System.IO.Path]::GetFileName($DumpFile) -replace '\.age$', '')

    Write-Host "[DECRYPT] Dang giai ma ban backup .age ve thu muc tam..."
    $decryptResult = Invoke-AgeDecryption -EncryptedFilePath $DumpFile -OutputDecryptedPath $tempPlaintextDump -IdentityFilePath $identityFile -AgeExePath $AgeExePath -MockCommandBlock $AgeMockCommandBlock

    $expectedPlainHash = if ($selectedManifest.PSObject.Properties['plaintextSha256']) { $selectedManifest.plaintextSha256 } else { $selectedManifest.sha256 }
    if ($decryptResult.Sha256 -ne $expectedPlainHash) {
        throw "[LOI GIAI MA] SHA-256 plaintext sau giai ma ($($decryptResult.Sha256)) KHONG KHOP voi manifest ($expectedPlainHash). Dung dien tap."
    }
    $restoreDumpFile = $tempPlaintextDump
    Write-Host "[DECRYPT] Giai ma thanh cong. SHA-256 plaintext khop 100% voi manifest."
}

# 3. Xác minh tính đọc được của file dump trước khi làm việc bằng pg_restore -l
Write-Host "[VERIFY] Dang kiem tra cau truc archive bang pg_restore -l (timeout: $VerifyTimeoutSeconds giay)..."
$tocTest = Invoke-ProcessWithTimeout -FilePath $resolvedPgRestore -Arguments @("-l", $restoreDumpFile) -TimeoutSeconds $VerifyTimeoutSeconds
if ($tocTest.TimedOut) {
    Write-Error "[LOI TOAN VEN] pg_restore -l vuot qua timeout $VerifyTimeoutSeconds giay va da bi terminate (H3). Dung dien tap."
    exit 1
}
if ($tocTest.ExitCode -ne 0) {
    Write-Error "[LOI TOAN VEN] File dump khong the doc duoc bang pg_restore (Ma thoat: $($tocTest.ExitCode)). Dung dien tap."
    exit 1
}

# 4. Xác định cấu hình đích & Nguồn để phòng vệ an toàn
if ([string]::IsNullOrWhiteSpace($TargetConnectionString)) {
    if (-not [string]::IsNullOrWhiteSpace($env:TARGET_PG_CONNECTION)) {
        $TargetConnectionString = $env:TARGET_PG_CONNECTION
    } elseif (-not [string]::IsNullOrWhiteSpace($env:ConnectionStrings__DefaultConnection)) {
        $TargetConnectionString = $env:ConnectionStrings__DefaultConnection
    } else {
        $TargetConnectionString = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres_password_123"
    }
}

$targetParams = Parse-PgConnectionString -RawConnection $TargetConnectionString

# Tự động nạp thông tin nguồn từ manifest JSON nếu chưa truyền -SourceConnectionString
if ([string]::IsNullOrWhiteSpace($SourceConnectionString)) {
    if ($null -ne $selectedManifest) {
        if (-not [string]::IsNullOrWhiteSpace($selectedManifest.sourceHost) -and -not [string]::IsNullOrWhiteSpace($selectedManifest.sourceDatabase)) {
            $SourceConnectionString = "Host=$($selectedManifest.sourceHost);Database=$($selectedManifest.sourceDatabase)"
            Write-Host "[SOURCE DETECT] Da xac dinh nguon tu manifest backup: $($selectedManifest.sourceHost) / $($selectedManifest.sourceDatabase)"
        }
    } else {
        $manifestPath = [System.IO.Path]::ChangeExtension($DumpFile, ".json")
        if (Test-Path -LiteralPath $manifestPath) {
            try {
                $manifestObj = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
                if (-not [string]::IsNullOrWhiteSpace($manifestObj.sourceHost) -and -not [string]::IsNullOrWhiteSpace($manifestObj.sourceDatabase)) {
                    $SourceConnectionString = "Host=$($manifestObj.sourceHost);Database=$($manifestObj.sourceDatabase)"
                    Write-Host "[SOURCE DETECT] Da xac dinh nguon tu manifest backup: $($manifestObj.sourceHost) / $($manifestObj.sourceDatabase)"
                }
            } catch {
                Write-Warning "[SOURCE DETECT] Khong doc duoc manifest JSON cua file dump."
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($SourceConnectionString) -and -not [string]::IsNullOrWhiteSpace($env:ConnectionStrings__DefaultConnection)) {
        $SourceConnectionString = $env:ConnectionStrings__DefaultConnection
        Write-Host "[SOURCE DETECT] Da lay thong tin nguon tu bien moi truong ConnectionStrings__DefaultConnection."
    }
}

$sourceParams = Parse-PgConnectionString -RawConnection $SourceConnectionString

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$drillRandom = [Guid]::NewGuid().ToString("N").Substring(0, 8)
if ([string]::IsNullOrWhiteSpace($TargetDatabase)) {
    $TargetDatabase = "eventpulse_drill_${timestamp}_${drillRandom}"
}

# Kiểm tra phòng vệ nghiêm ngặt (Deny-by-default với Allowlist)
Assert-SafeDrillEnvironment -TargetParams $targetParams -SourceParams $sourceParams -TargetDbName $TargetDatabase -AllowedHosts $AllowedHosts

Write-Host "[TARGET CHECK] May chu kiem thu: $($targetParams.Host):$($targetParams.Port)"
Write-Host "[TARGET CHECK] Database tam: $TargetDatabase"

$drillRunId = [Guid]::NewGuid().ToString("N")
$drillDbCreated = $false
$ownershipVerified = $false
$drillCompletedSuccessfully = $false
$oldPgPassword = $env:PGPASSWORD

try {
    $env:PGPASSWORD = $targetParams.Password

    # 5. Tạo Database tạm thời
    Write-Host "[DRILL INIT] Dang tao database tam '$TargetDatabase'..."
    $createDbQuery = "CREATE DATABASE `"$TargetDatabase`";"
    Invoke-PgSqlQuery -PsqlExe $resolvedPsql -ConnParams $targetParams -DatabaseName "postgres" -Query $createDbQuery | Out-Null
    $drillDbCreated = $true

    # 6. Đóng dấu nhận diện quyền sở hữu ngay lập tức trước khi restore
    Write-Host "[OWNERSHIP] Dang ghi nhan quyen so huu phien dien tap (ID: $drillRunId)..."
    $createOwnershipQuery = "CREATE TABLE IF NOT EXISTS ""__eventpulse_drill_ownership"" (""drill_run_id"" text PRIMARY KEY, ""created_at"" timestamp with time zone DEFAULT CURRENT_TIMESTAMP, ""machine_name"" text, ""created_by"" text); INSERT INTO ""__eventpulse_drill_ownership"" (""drill_run_id"", ""machine_name"", ""created_by"") VALUES ('$drillRunId', '$env:COMPUTERNAME', '$env:USERNAME');"
    Invoke-PgSqlQuery -PsqlExe $resolvedPsql -ConnParams $targetParams -DatabaseName $TargetDatabase -Query $createOwnershipQuery | Out-Null

    # 7. Thực hiện Restore vào Database tạm
    $dumpLeafName = Split-Path $restoreDumpFile -Leaf
    Write-Host "[RESTORE] Dang khoi phuc du lieu tu $dumpLeafName (timeout: $RestoreTimeoutSeconds giay)..."
    $restoreArgs = @(
        "-h", $targetParams.Host,
        "-p", $targetParams.Port.ToString(),
        "-U", $targetParams.Username,
        "-d", $TargetDatabase,
        "--no-owner",
        "--no-privileges",
        $restoreDumpFile
    )

    $restoreProcess = Invoke-ProcessWithTimeout -FilePath $resolvedPgRestore -Arguments $restoreArgs -TimeoutSeconds $RestoreTimeoutSeconds
    if ($restoreProcess.TimedOut) {
        throw "[LOI RESTORE TIMEOUT] pg_restore vuot qua $RestoreTimeoutSeconds giay va da bi terminate an toan (H3)."
    }
    $restoreResult = Assert-PgRestoreExitCode -ExitCode $restoreProcess.ExitCode
    Write-Host "[RESTORE] Lenh pg_restore hoan thanh: $($restoreResult.Message)"

    # 8. Xác minh quyền sở hữu sau khi restore
    $verifyOwnershipQuery = "SELECT count(*) FROM ""__eventpulse_drill_ownership"" WHERE ""drill_run_id"" = '$drillRunId';"
    $ownershipCount = (Invoke-PgSqlQuery -PsqlExe $resolvedPsql -ConnParams $targetParams -DatabaseName $TargetDatabase -Query $verifyOwnershipQuery).Trim()
    if ($ownershipCount -eq "1") {
        $ownershipVerified = $true
        Write-Host "[OWNERSHIP] Xac minh quyen so huu: DAT (Database thuoc phien drill $drillRunId)"
    } else {
        throw "Khong tim thay dau quyen so huu cua phien drill nay sau restore! Dung ngay de bao ve an toan."
    }

    # 9. KIỂM TRA TÍNH TOÀN VẸN ĐA CẤP ĐỘ (INTEGRITY CHECK DYNAMIC)
    Write-Host "=================================================================="
    Write-Host " BAT DAU DOI SOAT TINH TOAN VEN (DYNAMIC INTEGRITY CHECK)"
    Write-Host "=================================================================="

    # Cấp 1: Kiểm tra Migrations History
    $migrationQuery = 'SELECT count(*) FROM "__EFMigrationsHistory";'
    $migrationCountStr = (Invoke-PgSqlQuery -PsqlExe $resolvedPsql -ConnParams $targetParams -DatabaseName $TargetDatabase -Query $migrationQuery).Trim()
    $migrationCount = 0
    [int]::TryParse($migrationCountStr, [ref]$migrationCount) | Out-Null
    Write-Host "[SCHEMA CHECK] So luong Migrations da ap dung: $migrationCount"

    # Cấp 1.1: Kiểm tra số lượng Bảng nghiệp vụ
    $tableCountQuery = "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' AND LEFT(table_name, 2) <> '__';"
    $tableCountStr = (Invoke-PgSqlQuery -PsqlExe $resolvedPsql -ConnParams $targetParams -DatabaseName $TargetDatabase -Query $tableCountQuery).Trim()
    $tableCount = 0
    [int]::TryParse($tableCountStr, [ref]$tableCount) | Out-Null
    Write-Host "[SCHEMA CHECK] So luong bang nghiep vu (Public Tables): $tableCount"

    # Cấp 1.2: Kiểm tra Foreign Keys
    $fkQuery = "SELECT count(*) FROM information_schema.table_constraints WHERE constraint_type = 'FOREIGN KEY' AND table_schema='public';"
    $fkCountStr = (Invoke-PgSqlQuery -PsqlExe $resolvedPsql -ConnParams $targetParams -DatabaseName $TargetDatabase -Query $fkQuery).Trim()
    $fkCount = 0
    [int]::TryParse($fkCountStr, [ref]$fkCount) | Out-Null
    Write-Host "[SCHEMA CHECK] So luong rang buoc Foreign Keys: $fkCount"

    # Cấp 1.3: Kiểm tra Indexes
    $indexQuery = "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public';"
    $indexCountStr = (Invoke-PgSqlQuery -PsqlExe $resolvedPsql -ConnParams $targetParams -DatabaseName $TargetDatabase -Query $indexQuery).Trim()
    $indexCount = 0
    [int]::TryParse($indexCountStr, [ref]$indexCount) | Out-Null
    Write-Host "[SCHEMA CHECK] So luong Indexes: $indexCount"

    # Xác thực toàn bộ điều kiện schema/FK/Index bắt buộc
    Assert-DatabaseIntegrityCounts -MigrationCount $migrationCount -TableCount $tableCount -FkCount $fkCount -IndexCount $indexCount | Out-Null

    # Cấp 2: Thống kê số lượng bản ghi theo từng bảng (Row Counts)
    Write-Host "------------------------------------------------------------------"
    Write-Host "[DATA CHECK] So luong ban ghi tung bang sau khoi phuc:"

    $getTablesQuery = "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' AND LEFT(table_name, 2) <> '__' ORDER BY table_name;"
    $tablesList = (Invoke-PgSqlQuery -PsqlExe $resolvedPsql -ConnParams $targetParams -DatabaseName $TargetDatabase -Query $getTablesQuery) -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $tableStats = @()
    $failedTableCount = 0
    $failedTableDetails = @()

    foreach ($tbl in $tablesList) {
        $cleanTbl = $tbl.Trim()
        $cntQuery = 'SELECT count(*) FROM "' + $cleanTbl + '";'
        try {
            $rowCnt = (Invoke-PgSqlQuery -PsqlExe $resolvedPsql -ConnParams $targetParams -DatabaseName $TargetDatabase -Query $cntQuery).Trim()
            $tableStats += [PSCustomObject]@{ TableName = $cleanTbl; RowCount = [int]$rowCnt }
            Write-Host ("  - {0,-35} : {1,6} ban ghi" -f $cleanTbl, $rowCnt)
        } catch {
            $failedTableCount++
            $errDetail = "$cleanTbl : $($_.Exception.Message)"
            $failedTableDetails += $errDetail
            # -ErrorAction Continue de khong gian doan viec dem cac bang con lai
            Write-Error "  - [LOI DOI SOAT] Khong the dem so ban ghi cua bang $errDetail" -ErrorAction Continue
        }
    }

    # S55-01: Bắt buộc dừng và ném lỗi nếu có bất kỳ bảng nghiệp vụ nào không thể đếm số bản ghi
    if ($failedTableCount -gt 0) {
        $summaryErrors = $failedTableDetails -join "; "
        throw "Doi soat that bai: Co $failedTableCount bang nghiep vu khong the truy van so luong ban ghi ($summaryErrors). Dung dien tap de bao ve du lieu phuc vu dieu tra."
    }

    # S55-BASELINE: Đối chiếu row counts với baseline ghi trong manifest lúc backup (nếu có)
    $baselineCompare = @{ Compared = $false; MismatchCount = 0; Mismatches = @() }
    if ($null -ne $selectedManifest -and $selectedManifest.PSObject.Properties['tableRowCounts'] -and $null -ne $selectedManifest.tableRowCounts) {
        $baseline = $selectedManifest.tableRowCounts
        $baselineCompare = Compare-S55BaselineRowCounts -Baseline $baseline -TableStats $tableStats -MigrationCount $migrationCount
        if ($baselineCompare.MismatchCount -gt 0) {
            throw "Doi soat baseline that bai: $($baselineCompare.MismatchCount) bang co so dong khac biet so voi manifest luc backup: $($baselineCompare.Mismatches -join '; ')"
        }
        Write-Host "[BASELINE] Doi soat row counts voi manifest: DAT — toan bo bang khop baseline."
    } else {
        Write-Warning "[BASELINE] Manifest khong co tableRowCounts — bo qua buoc doi soat baseline (chi doi soat cau truc)."
    }

    # Đo lường và kiểm tra ngưỡng RTO bắt buộc (<= 900 giây)
    $rtoSeconds = [math]::Round(((Get-Date) - $startTime).TotalSeconds, 2)
    Assert-RtoThreshold -RtoSeconds $rtoSeconds -MaxAllowedSeconds $MaxRtoSeconds | Out-Null
    Write-Host "------------------------------------------------------------------"
    Write-Host "[DO LUONG RTO] Thoi gian thuc thi dien tap khoi phuc do duoc: $rtoSeconds giay (Muc tieu van hanh: <= $MaxRtoSeconds giay / $([math]::Round($MaxRtoSeconds / 60, 1)) phut)."
    Write-Host "=================================================================="
    Write-Host " BAO CAO KET QUA DIEN TAP KHOI PHUC (S-55 DRILL REPORT)"
    Write-Host " - Trang thai khoi phuc  : THANH CONG"
    Write-Host " - Database dien tap     : $TargetDatabase"
    Write-Host " - Drill Session ID      : $drillRunId"
    Write-Host " - Tong so migrations    : $migrationCount"
    Write-Host " - Tong so bang          : $tableCount"
    Write-Host " - Rang buoc FK          : $fkCount"
    Write-Host " - RTO do luong          : $rtoSeconds s"
    Write-Host " [XAC NHAN GIOI HAN] Doi soat xac minh tinh toan ven ve cau truc schema, migrations, rang buoc khoa ngoai, chi muc va so luong ban ghi."
    Write-Host "                     Khong tu coi so luong bang nhau la bang chung tuyet doi du lieu nghiep vu dong nhat."
    Write-Host "                     Can chay test suite API tren database drill de kiem chung logic ung dung."
    Write-Host "=================================================================="

    # Ghi kết quả diễn tập ra ResultFile (nếu có) để runbook/scheduler đọc lại
    if (-not [string]::IsNullOrWhiteSpace($ResultFile)) {
        try {
            $drillResult = [PSCustomObject]@{
                generatedAtUtc   = (Get-Date).ToUniversalTime().ToString("o")
                status           = "Success"
                targetDatabase   = $TargetDatabase
                drillRunId       = $drillRunId
                dumpFile         = (Split-Path $restoreDumpFile -Leaf)
                rtoSeconds       = $rtoSeconds
                maxRtoSeconds    = $MaxRtoSeconds
                migrationCount   = $migrationCount
                tableCount       = $tableCount
                fkCount          = $fkCount
                indexCount       = $indexCount
                tableStats       = $tableStats
                baselineComparison = $baselineCompare
            }
            $resultDir = Split-Path $ResultFile -Parent
            if (-not [string]::IsNullOrWhiteSpace($resultDir) -and -not (Test-Path -LiteralPath $resultDir)) {
                New-Item -ItemType Directory -Path $resultDir -Force | Out-Null
            }
            $drillResult | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResultFile -Encoding utf8
            Write-Host "[RESULT FILE] Da ghi ket qua dien tap: $ResultFile"
        } catch {
            Write-Warning "[RESULT FILE] Khong the ghi ket qua dien tap: $_"
        }
    }

    # ĐÁNH DẤU HOÀN THÀNH TOÀN BỘ CÁC BƯỚC ĐẠT
    $drillCompletedSuccessfully = $true

} catch {
    # -ErrorAction Continue de ghi duoc ResultFile that bai va cac canh bao an toan phia sau
    Write-Error "[DIEN TAP THAT BAI] $_" -ErrorAction Continue
    # Giữ nguyên database để điều tra khi có lỗi
    Write-Warning "[AN TOAN] Database tam '$TargetDatabase' duoc GIU LAI de phuc vu dieu tra loi."
    if (-not [string]::IsNullOrWhiteSpace($ResultFile)) {
        try {
            $failResult = [PSCustomObject]@{
                generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
                status         = "Failed"
                targetDatabase = $TargetDatabase
                drillRunId     = $drillRunId
                error          = $_.Exception.Message
            }
            $failResult | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ResultFile -Encoding utf8
        } catch {
            Write-Warning "[RESULT FILE] Khong the ghi ket qua that bai: $_"
        }
    }
    exit 1
} finally {
    # 10. Dọn dẹp an toàn có kiểm chứng ĐẦY ĐỦ TẤT CẢ ĐIỀU KIỆN
    if ($drillDbCreated) {
        if ($KeepDrillDb) {
            Write-Host "[CLEANUP] Tuy chon -KeepDrillDb duoc bat. Database '$TargetDatabase' duoc giu lai phuc vu kiem tra."
        } elseif ($ownershipVerified -and $drillCompletedSuccessfully) {
            Write-Host "[CLEANUP] Dien tap hoan tat thanh cong va xac minh quyen so huu ($drillRunId). Dang don dep database tam '$TargetDatabase'..."
            try {
                $dropDbQuery = "DROP DATABASE `"$TargetDatabase`" WITH (FORCE);"
                Invoke-PgSqlQuery -PsqlExe $resolvedPsql -ConnParams $targetParams -DatabaseName "postgres" -Query $dropDbQuery | Out-Null
                Write-Host "[CLEANUP] Da xoa database tam sach se."
            } catch {
                Write-Warning "[CLEANUP THAT BAI] Khong the tu dong DROP database '$TargetDatabase': $_"
                Write-Warning "[CLEANUP CANH BAO] Database '$TargetDatabase' van con ton tai tren host '$($targetParams.Host):$($targetParams.Port)'. Vui long don dep thu cong!"
            }
        } else {
            Write-Warning "[CLEANUP AN TOAN] Giu nguyen database '$TargetDatabase' de dieu tra (Dieu kien an toan: OwnershipVerified=$ownershipVerified, DrillCompletedSuccessfully=$drillCompletedSuccessfully)."
            Write-Warning "[CLEANUP THONG TIN] Tai nguyen chua don dep: Database '$TargetDatabase' tren host '$($targetParams.Host):$($targetParams.Port)'."
        }
    }

    if ($null -ne $oldPgPassword) {
        $env:PGPASSWORD = $oldPgPassword
    } else {
        Remove-Item env:PGPASSWORD -ErrorAction SilentlyContinue
    }

    # Xóa bản giải mã tạm (nếu có) để không để lại dữ liệu plaintext
    if (-not [string]::IsNullOrWhiteSpace($decryptTempDir) -and (Test-Path -LiteralPath $decryptTempDir)) {
        Remove-Item -Path $decryptTempDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "[CLEANUP] Da xoa thu muc giai ma tam: $decryptTempDir"
    }
}

exit 0
