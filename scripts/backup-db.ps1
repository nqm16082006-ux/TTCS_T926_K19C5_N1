<#
.SYNOPSIS
    S-55: Script sao lưu cơ sở dữ liệu PostgreSQL an toàn và tự động thực thi chính sách retention 14 bản.

.DESCRIPTION
    - Sử dụng pg_dump định dạng Custom (-Fc) có nén và checksum nội tại.
    - Cơ chế Staging an toàn: Xuất ra file tạm thời trong quá trình dump/verify; chỉ công bố file chính thức khi xác minh hoàn tất.
    - Đặt tên file theo định dạng timestamp: eventpulse_backup_yyyyMMdd_HHmmss.dump.
    - Tạo file manifest JSON đi kèm chứa SHA-256, kích thước byte, thời điểm tạo UTC, metadata nguồn và trạng thái xác thực.
    - Xác minh tính toàn vẹn của archive bằng pg_restore -l trước khi đánh dấu là backup hợp lệ.
    - Thực thi chính sách Retention: Chỉ giữ lại tối đa 14 bản backup hợp lệ gần nhất.
    - Tiêu chí hợp lệ nghiêm ngặt: Phải có cả dump và manifest khớp SHA-256 và status=VerifiedValid. Không công nhận backup chỉ dựa trên kích thước.
    - Bảo vệ an toàn: Nếu backup mới thất bại, KHÔNG xóa bất kỳ bản backup cũ nào; dọn dẹp file tạm dở dang.
    - Bảo mật: Không in mật khẩu hoặc connection string chứa bí mật ra màn hình/log.
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$ConnectionString,

    [Parameter()]
    [string]$BackupDir = "",

    [Parameter()]
    [int]$MaxRetention = 14,

    [Parameter()]
    [int]$DumpTimeoutSeconds = 300,

    [Parameter()]
    [string]$PgDumpPath,

    [Parameter()]
    [string]$PgRestorePath,

    [Parameter()]
    [string]$PsqlPath,

    [Parameter()]
    [string]$PgToolMode = "",

    [Parameter()]
    [string]$PgDockerImage = "",

    [Parameter()]
    [int]$VerifyTimeoutSeconds = 300,

    [Parameter()]
    [string]$LogFile,

    [Parameter()]
    [switch]$TestOnlyRetention,

    [Parameter()]
    [bool]$SimulateFailureForRetentionTest = $false,

    [Parameter()]
    [switch]$TestOnlyTimeout,

    [Parameter()]
    [hashtable]$TimeoutTestInputs,

    [Parameter()]
    [switch]$EnableOffsite,

    [Parameter()]
    [string]$AgeRecipientKey,

    [Parameter()]
    [string]$R2BucketName,

    [Parameter()]
    [string]$R2AccountId,

    [Parameter()]
    [string]$R2AccessKeyId,

    [Parameter()]
    [string]$R2SecretAccessKey,

    [Parameter()]
    [string]$R2Prefix = "",

    [Parameter()]
    [string]$AgeExePath,

    [Parameter()]
    [string]$RcloneExePath,

    [Parameter()]
    [scriptblock]$OffsiteMockCommandBlock,

    [Parameter()]
    [switch]$TestOnlyOffsite,

    [Parameter()]
    [hashtable]$OffsiteTestInputs,

    [Parameter()]
    [switch]$TestOnlyBaseline,

    [Parameter()]
    [hashtable]$BaselineTestInputs
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot trong param default khong duoc nap dung gio trong PowerShell 5.1
# (advanced script), nen tinh duong dan o day de tuong thich ca 5.1 lan 7.x.
if ([string]::IsNullOrWhiteSpace($BackupDir)) {
    $BackupDir = Join-Path $PSScriptRoot "..\artifacts\database-backups"
}

# Nạp module dùng chung S-55 (Get-PgToolExecutable, Parse-PgConnectionString, Invoke-PgSqlQuery, Invoke-ProcessWithTimeout, Test-S55BackupValidity, Assert-SafeDrillEnvironment)
. (Join-Path $PSScriptRoot "s55-common.ps1")

# S-55 Docker mode: chon Native hoac Docker cho pg_dump/pg_restore/psql
# (tham so -PgToolMode, hoac env S55_PG_TOOL_MODE; mac dinh Native).
$effectivePgToolMode = Resolve-S55PgToolMode -ToolMode $PgToolMode
Set-S55PgToolMode -Mode $effectivePgToolMode -DockerImage $PgDockerImage
Write-Host "[TOOL MODE] PostgreSQL client mode: $effectivePgToolMode"

$transcriptStarted = $false
if (-not [string]::IsNullOrWhiteSpace($LogFile)) {
    try {
        $logDir = Split-Path $LogFile -Parent
        if (-not [string]::IsNullOrWhiteSpace($logDir) -and -not (Test-Path -LiteralPath $logDir)) {
            New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        }
        Start-Transcript -Path $LogFile -Append | Out-Null
        $transcriptStarted = $true
        Write-Host "[LOGGING] Transcript bat dau tai: $LogFile"
    } catch {
        Write-Warning "[LOGGING] Khong the bat dau transcript tai '$LogFile': $_"
    }
}

# ==============================================================================
# HÀM BỔ TRỢ: THỰC THI PG_DUMP VỚI TIMEOUT AN TOÀN (H3 GUARD)
# ==============================================================================
function Invoke-PgDumpWithTimeout {
    param(
        [Parameter(Mandatory)]
        [string]$PgDumpExe,

        [Parameter(Mandatory)]
        [string[]]$DumpArgs,

        [Parameter()]
        [int]$TimeoutSeconds = 300,

        [Parameter()]
        [hashtable]$ConnParams,

        [Parameter()]
        [string]$HostDir,

        [Parameter()]
        [scriptblock]$MockCommandBlock
    )

    # Docker mode: uy quyen cho Invoke-PgToolCommand (mount HostDir -> /s55work,
    # mat khau qua --env-file tam, remap duong dan -f ve container).
    if ($script:S55PgToolMode -eq 'Docker') {
        Write-Host "[EXEC] Dang thuc thi pg_dump trong Docker container (Timeout: $TimeoutSeconds giay)..."
        $res = Invoke-PgToolCommand -ToolName "pg_dump" `
            -ToolArgs $DumpArgs `
            -ConnParams $ConnParams `
            -Mode 'Docker' `
            -DockerImage $script:S55PgDockerImage `
            -HostDir $HostDir `
            -TimeoutSeconds $TimeoutSeconds `
            -MockCommandBlock $MockCommandBlock
        return $res.ExitCode
    }

    Write-Host "[EXEC] Dang thuc thi pg_dump (Timeout: $TimeoutSeconds giay)..."

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $PgDumpExe
    $escapedArgs = ($DumpArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
    $psi.Arguments = $escapedArgs
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    $hasExited = $process.WaitForExit($TimeoutSeconds * 1000)

    if (-not $hasExited) {
        Write-Warning "[TIMEOUT] pg_dump vuot qua thoi gian gioi han ($TimeoutSeconds giay)! Dang tien hanh dung tien trinh..."
        try {
            $process.Kill()
            $process.WaitForExit(5000)
        } catch {
            Write-Warning "[TIMEOUT] Khong the kill tien trinh: $_"
        }
        throw "pg_dump bi timeout sau $TimeoutSeconds giay va da bi dung an toan."
    }

    if ($process.ExitCode -ne 0) {
        throw "pg_dump that bai voi ma thoat: $($process.ExitCode)"
    }

    return $process.ExitCode
}

# ==============================================================================
# HÀM BỔ TRỢ: CHÍNH SÁCH RETENTION 14 BẢN (XÁC MINH MANIFEST & SHA-256)
# ==============================================================================
function Invoke-BackupRetention {
    param(
        [Parameter(Mandatory)]
        [string]$DirectoryPath,

        [Parameter()]
        [int]$RetentionLimit = 14,

        [Parameter()]
        [bool]$IsCurrentBackupFailed = $false
    )

    if ($IsCurrentBackupFailed) {
        Write-Warning "[RETENTION] Backup moi that bai! Theo nguyen tac an toan, KHONG xoa bat ky ban backup cu nao."
        return @{ Retained = @(); Deleted = @(); DeletedCount = 0; Status = "AbortedDueToFailure" }
    }

    if (-not (Test-Path -LiteralPath $DirectoryPath)) {
        return @{ Retained = @(); Deleted = @(); Status = "DirectoryNotFound" }
    }

    # Chỉ tìm các file khớp đúng quy ước đặt tên chính thức: eventpulse_backup_yyyyMMdd_HHmmss.dump
    $dumpFiles = Get-ChildItem -Path $DirectoryPath -File |
        Where-Object { $_.Name -match '^eventpulse_backup_\d{8}_\d{6}\.dump$' }

    $validBackups = @()
    foreach ($dump in $dumpFiles) {
        $manifestPath = [System.IO.Path]::ChangeExtension($dump.FullName, ".json")
        $isValid = $false

        # Điều kiện bắt buộc: Phải có manifest JSON đi kèm
        if (Test-Path -LiteralPath $manifestPath) {
            try {
                $manifestContent = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
                $expectedSha = if ($manifestContent.PSObject.Properties['plaintextSha256']) { $manifestContent.plaintextSha256 } else { $manifestContent.sha256 }
                # Kiểm tra đầy đủ: status VerifiedValid, kích thước > 1024, tên file khớp và SHA-256 khớp
                if ($manifestContent.status -eq "VerifiedValid" -and
                    $dump.Length -gt 1024 -and
                    $manifestContent.backupFile -eq $dump.Name -and
                    -not [string]::IsNullOrWhiteSpace($expectedSha)) {
                    
                    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $dump.FullName).Hash
                    if ($actualHash -eq $expectedSha) {
                        $isValid = $true
                    } else {
                        Write-Warning "[RETENTION] Bo qua file dump co SHA-256 khong khop manifest: $($dump.Name)"
                    }
                }
            } catch {
                $isValid = $false
            }
        }

        # TUYỆT ĐỐI KHÔNG công nhận file hợp lệ nếu thiếu manifest hoặc manifest sai
        if ($isValid) {
            $validBackups += $dump
        } else {
            Write-Warning "[RETENTION] Bo qua file khong du dieu kien xac thuc hop le: $($dump.Name)"
        }
    }

    # Sắp xếp các bản backup hợp lệ theo thời gian tạo giảm dần (mới nhất lên đầu)
    $sortedValid = $validBackups | Sort-Object -Property LastWriteTime -Descending

    $retained = @()
    $deleted = @()

    if ($sortedValid.Count -le $RetentionLimit) {
        $retained = $sortedValid
        Write-Host "[RETENTION] Tong so ban backup hop le hien tai: $($sortedValid.Count) <= $RetentionLimit. Khong can don dep."
    } else {
        $retained = $sortedValid | Select-Object -First $RetentionLimit
        $toDelete = $sortedValid | Select-Object -Skip $RetentionLimit

        foreach ($file in $toDelete) {
            $manifestToDelete = [System.IO.Path]::ChangeExtension($file.FullName, ".json")
            $encryptedToDelete = $file.FullName + ".age"
            Write-Host "[RETENTION] Dang xoa ban backup cu qua han: $($file.Name)"
            Remove-Item -LiteralPath $file.FullName -Force
            if (Test-Path -LiteralPath $manifestToDelete) {
                Remove-Item -LiteralPath $manifestToDelete -Force
            }
            if (Test-Path -LiteralPath $encryptedToDelete) {
                Remove-Item -LiteralPath $encryptedToDelete -Force
            }
            $deleted += $file.Name
        }
    }

    return @{
        TotalValidCount = $sortedValid.Count
        RetainedCount   = $retained.Count
        DeletedCount    = $deleted.Count
        DeletedFiles    = $deleted
        Status          = "Success"
    }
}

# ==============================================================================
# HÀM BỔ TRỢ: RETENTION R2 SAU KHI UPLOAD DA VERIFIEDUPLOADED (F5)
# Loi retention transient (mang, quyen) KHONG duoc bien ban backup da upload
# va xac minh thanh thanh "that bai" — chi canh bao va tra trang thai ro rang.
# ==============================================================================
function Invoke-S55RemoteRetentionSafe {
    param(
        [Parameter(Mandatory)]
        [hashtable]$R2Config,

        [Parameter()]
        [int]$RetentionLimit = 14,

        [Parameter()]
        [string]$RcloneExePath,

        [Parameter()]
        [scriptblock]$MockCommandBlock
    )

    try {
        return Invoke-R2RemoteRetention -R2Config $R2Config `
            -RetentionLimit $RetentionLimit `
            -IsUploadFailed $false `
            -RcloneExePath $RcloneExePath `
            -MockCommandBlock $MockCommandBlock
    } catch {
        Write-Warning "[OFFSITE RETENTION FAILED] Khong the thuc thi retention tren R2: $($_.Exception.Message)"
        Write-Warning "[OFFSITE RETENTION FAILED] Ban backup moi da upload va xac minh (VerifiedUploaded) — loi nay KHONG lam mat ban backup. Co the con ton dong object cu qua han; hay chay lai retention thu cong sau khi xu ly nguyen nhan."
        return @{
            Status        = "RetentionFailedNonBlocking"
            RetainedCount = 0
            DeletedCount  = 0
            DeletedFiles  = @()
            Error         = $_.Exception.Message
        }
    }
}

# ==============================================================================
# NẾU CHỈ KIỂM THỬ RETENTION (CHẾ ĐỘ TEST CÔ LẬP)
# ==============================================================================
if ($TestOnlyRetention) {
    Write-Host "[TEST MODE] Dang chay kiem thu co lap chinh sach Retention tren: $BackupDir"
    $retentionResult = Invoke-BackupRetention -DirectoryPath $BackupDir -RetentionLimit $MaxRetention -IsCurrentBackupFailed $SimulateFailureForRetentionTest
    Write-Host "[TEST MODE] Ket qua:" ($retentionResult | ConvertTo-Json -Compress)
    return $retentionResult
}

if ($TestOnlyTimeout) {
    Write-Host "[TEST MODE] Dang chay kiem thu co lap timeout pg_dump..."
    return (Invoke-PgDumpWithTimeout -PgDumpExe $TimeoutTestInputs.Exe -DumpArgs $TimeoutTestInputs.Args -TimeoutSeconds $TimeoutTestInputs.TimeoutSeconds)
}

if ($TestOnlyOffsite) {
    Write-Host "[TEST MODE] Dang chay kiem thu co lap offsite backup..."
    $uploadModule = Join-Path $PSScriptRoot "backup-upload-r2.ps1"
    . $uploadModule
    if ($null -ne $OffsiteTestInputs) {
        if ($OffsiteTestInputs.ContainsKey("ValidateConfig")) {
            return Validate-R2Config -Config $OffsiteTestInputs.ValidateConfig
        }
        if ($OffsiteTestInputs.ContainsKey("ValidateAgeKey")) {
            return Validate-AgeRecipientKey -RecipientKey $OffsiteTestInputs.ValidateAgeKey
        }
        if ($OffsiteTestInputs.ContainsKey("EncryptTest")) {
            $inp = $OffsiteTestInputs.EncryptTest
            return Invoke-AgeEncryption -InputDumpPath $inp.InputDump -OutputEncryptedPath $inp.OutputEnc -RecipientKey $inp.Key -MockCommandBlock $inp.MockBlock
        }
        if ($OffsiteTestInputs.ContainsKey("ManifestTest")) {
            $inp = $OffsiteTestInputs.ManifestTest
            return Assert-BackupManifestV2Integrity -ManifestFilePath $inp.ManifestPath -ExpectedDumpPath $inp.ExpectedDump -ExpectedEncryptedPath $inp.ExpectedEnc
        }
        if ($OffsiteTestInputs.ContainsKey("UploadTest")) {
            $inp = $OffsiteTestInputs.UploadTest
            return Invoke-R2BackupUpload -EncryptedFilePath $inp.EncFile -ManifestFilePath $inp.ManifestFile -R2Config $inp.Config -MockCommandBlock $inp.MockBlock
        }
        if ($OffsiteTestInputs.ContainsKey("RetentionTest")) {
            $inp = $OffsiteTestInputs.RetentionTest
            return Invoke-R2RemoteRetention -R2Config $inp.Config -RetentionLimit $inp.Limit -IsUploadFailed $inp.IsUploadFailed -MockCommandBlock $inp.MockBlock
        }
        if ($OffsiteTestInputs.ContainsKey("RetentionSafeTest")) {
            $inp = $OffsiteTestInputs.RetentionSafeTest
            return Invoke-S55RemoteRetentionSafe -R2Config $inp.Config -RetentionLimit $inp.Limit -RcloneExePath $inp.RcloneExe -MockCommandBlock $inp.MockBlock
        }
    }
    return @{ Status = "OffsiteTestReady" }
}

if ($TestOnlyBaseline) {
    Write-Host "[TEST MODE] Dang chay kiem thu co lap baseline..."
    if ($null -ne $BaselineTestInputs) {
        return (Get-S55DatabaseBaselineRowCounts @BaselineTestInputs)
    }
    return @{ Status = "BaselineTestReady" }
}

# ==============================================================================
# LUỒNG CHÍNH: SAO LƯU DATABASE THỰC TẾ
# ==============================================================================
Write-Host "=================================================================="
Write-Host " S-55: SAO LUU CO SO DU LIEU POSTGRESQL (DAILY BACKUP SCRIPT)"
Write-Host "=================================================================="

# 1. Kiểm tra công cụ pg_dump và pg_restore
if ($effectivePgToolMode -eq 'Docker') {
    # Docker mode: client chay trong container, khong can cai PostgreSQL tren may
    if (-not (Get-Command $script:S55PgDockerExe -ErrorAction SilentlyContinue)) {
        Write-Error "[LOI MOI TRUONG] PgToolMode=Docker nhung khong tim thay '$($script:S55PgDockerExe)' trong PATH. Vui long kiem tra Docker."
        exit 1
    }
    $versionResult = Invoke-PgToolCommand -ToolName "pg_dump" -ToolArgs @('--version') `
        -Mode 'Docker' -DockerImage $script:S55PgDockerImage -CaptureOutput -TimeoutSeconds 600
    $pgDumpVersion = (($versionResult.Output -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1) -join '').Trim()
    Write-Host "[TOOL CHECK] Docker mode: pg_dump qua image '$($script:S55PgDockerImage)' — $pgDumpVersion"
} else {
    $resolvedPgDump = Get-PgToolExecutable -ToolName "pg_dump" -ExplicitPath $PgDumpPath
    $resolvedPgRestore = Get-PgToolExecutable -ToolName "pg_restore" -ExplicitPath $PgRestorePath

    if (-not $resolvedPgDump) {
        Write-Error "[LOI MOI TRUONG] Khong tim thay 'pg_dump' trong PATH hoac cac thu muc mac dinh. Vui long cai dat PostgreSQL 18 Client Tools, cung cap qua -PgDumpPath, hoac chay che do Docker (-PgToolMode Docker)."
        exit 1
    }

    if (-not $resolvedPgRestore) {
        Write-Error "[LOI MOI TRUONG] Khong tim thay 'pg_restore' trong PATH hoac cac thu muc mac dinh. Vui long cai dat PostgreSQL 18 Client Tools, cung cap qua -PgRestorePath, hoac chay che do Docker (-PgToolMode Docker)."
        exit 1
    }

    $pgDumpVersion = & $resolvedPgDump --version
    Write-Host "[TOOL CHECK] Da tim thay pg_dump: $resolvedPgDump"
    Write-Host "[TOOL CHECK] Phien ban: $pgDumpVersion"
}

# 2. Xác định Connection String an toàn
if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    if (-not [string]::IsNullOrWhiteSpace($env:ConnectionStrings__DefaultConnection)) {
        $ConnectionString = $env:ConnectionStrings__DefaultConnection
    } elseif (-not [string]::IsNullOrWhiteSpace($env:TARGET_PG_CONNECTION)) {
        $ConnectionString = $env:TARGET_PG_CONNECTION
    }
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    Write-Error "[LOI CAU HINH] Khong tim thay ConnectionString. Vui long cung cap chuoi ket noi an toan qua tham so hoac bien moi truong."
    exit 1
}

$pgParams = Parse-PgConnectionString -RawConnection $ConnectionString
if ([string]::IsNullOrWhiteSpace($pgParams.Database)) {
    Write-Error "[LOI CAU HINH] Khong the xac dinh ten Database tu chuoi ket noi."
    exit 1
}

Write-Host "[TARGET DB] May chu: $($pgParams.Host):$($pgParams.Port)"
Write-Host "[TARGET DB] Co so du lieu: $($pgParams.Database)"
Write-Host "[TARGET DB] Nguoi dung: $($pgParams.Username)"

# 3. Chuẩn bị thư mục sao lưu
if (-not (Test-Path -LiteralPath $BackupDir)) {
    New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

# Chống đụng tên khi 2 lần chạy trong cùng 1 giây (M5)
$collisionSuffix = 0
$finalDumpFilePath = Join-Path $BackupDir "eventpulse_backup_$timestamp.dump"
while (Test-Path -LiteralPath $finalDumpFilePath) {
    $collisionSuffix++
    $finalDumpFilePath = Join-Path $BackupDir ("eventpulse_backup_${timestamp}_{0}.dump" -f $collisionSuffix)
}
$finalDumpFileName = Split-Path $finalDumpFilePath -Leaf
$finalManifestPath = [System.IO.Path]::ChangeExtension($finalDumpFilePath, ".json")

# Đặt tên file staging tạm thời trong quá trình thực hiện
$stagingDumpFileName = [System.IO.Path]::GetFileNameWithoutExtension($finalDumpFileName) + "_in_progress.dump"
$stagingDumpFilePath = Join-Path $BackupDir $stagingDumpFileName
$stagingManifestPath = Join-Path $BackupDir ([System.IO.Path]::GetFileNameWithoutExtension($finalDumpFileName) + "_in_progress.json")

Write-Host "[BACKUP] Bat dau sao luu vao file tam: $stagingDumpFileName"

$backupSuccess = $false
$oldPgPassword = $env:PGPASSWORD

try {
    # Truyền mật khẩu qua biến môi trường phiên làm việc (không in ra log)
    $env:PGPASSWORD = $pgParams.Password

    $dumpArgs = @(
        "-h", $pgParams.Host,
        "-p", $pgParams.Port.ToString(),
        "-U", $pgParams.Username,
        "-d", $pgParams.Database,
        "-Fc",
        "-f", $stagingDumpFilePath
    )

    # Ánh xạ SSL mode kiểu Npgsql ("VerifyFull") sang giá trị libpq ("verify-full")
    $libpqSslMode = ConvertTo-LibPqSslMode -SslMode $pgParams.SslMode

    # Npgsql "Trust Server Certificate=true" => libpq "require" (mã hóa nhưng không xác minh chứng chỉ)
    if ($libpqSslMode -in @("verify-full", "verify-ca") -and $pgParams.TrustServerCertificate) {
        $libpqSslMode = "require"
    }

    if (-not [string]::IsNullOrWhiteSpace($libpqSslMode) -and $libpqSslMode -notin @("prefer", "allow")) {
        $env:PGSSLMODE = $libpqSslMode
        # Trên Linux dùng kho chứng chỉ hệ thống (không tồn tại ~/.postgresql/root.crt trong container)
        if ($libpqSslMode -in @("verify-full", "verify-ca") -and $IsLinux) {
            $env:PGSSLROOTCERT = "system"
        }
    }

    Invoke-PgDumpWithTimeout -PgDumpExe $resolvedPgDump -DumpArgs $dumpArgs -TimeoutSeconds $DumpTimeoutSeconds -ConnParams $pgParams -HostDir $BackupDir | Out-Null

    if (-not (Test-Path -LiteralPath $stagingDumpFilePath)) {
        throw "Khong tim thay file backup tam sau khi pg_dump hoan thanh."
    }

    $dumpItem = Get-Item -LiteralPath $stagingDumpFilePath
    if ($dumpItem.Length -le 1024) {
        throw "File backup co kich thuoc qua nho ($($dumpItem.Length) bytes), nghi ngo dump rong hoac bi loi."
    }

    # 4. Xác minh tính toàn vẹn của file dump bằng pg_restore -l (TOC List)
    Write-Host "[VERIFY] Dang kiem tra cau truc archive bang pg_restore -l (timeout: $VerifyTimeoutSeconds giay)..."
    $restoreTest = Invoke-PgProcessWithTimeout -ToolName "pg_restore" -ToolArgs @("-l", $stagingDumpFilePath) -NativeExePath $resolvedPgRestore -TimeoutSeconds $VerifyTimeoutSeconds -HostDir $BackupDir -ReadOnlyHostDir

    if ($restoreTest.TimedOut) {
        throw "pg_restore -l vuot qua timeout $VerifyTimeoutSeconds giay va da bi terminate an toan (H3)."
    }
    if ($restoreTest.ExitCode -ne 0) {
        throw "pg_restore -l tu choi file archive (Ma thoat: $($restoreTest.ExitCode)). File backup bi loi hoac khong doc duoc."
    }

    # 5. Tính toán SHA-256 của file staging
    $sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $stagingDumpFilePath).Hash
    $backupId = "ep-backup-${timestamp}-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"

    # 5.1 Thu thập baseline row counts vào manifest (bắt buộc - fail-closed)
    # Ghi chú về snapshot consistency:
    # pg_dump và psql chạy qua các tiến trình CLI riêng biệt. Baseline row counts được thu thập
    # ngay sau khi tạo dump hoàn tất. Trong điều kiện bảo trì hoặc tải tĩnh, baseline phản ánh
    # chính xác trạng thái dữ liệu của bản backup. Tuy nhiên, nếu có các giao dịch ghi đồng thời
    # diễn ra giữa thời điểm pg_dump hoàn tất và psql truy vấn count(*), có thể xuất hiện sai lệch
    # nhỏ giữa dump và baseline. Kiến trúc hiện tại không cam kết transaction snapshot consistency
    # tuyệt đối giữa 2 lệnh CLI riêng biệt; không tuyên bố đã bảo đảm snapshot consistency.
    Write-Host "[BASELINE] Dang thu thap baseline row counts tu CSDL nguon (fail-closed)..."
    $resolvedPsql = Get-PgToolExecutable -ToolName "psql" -ExplicitPath $PsqlPath
    if ($effectivePgToolMode -ne 'Docker' -and [string]::IsNullOrWhiteSpace($resolvedPsql)) {
        throw "[ABORT] Khong the tao ban backup: Khong tim thay cong cu 'psql' de thu thap baseline row counts."
    }

    $tableRowCounts = Get-S55DatabaseBaselineRowCounts -PsqlExe $resolvedPsql `
        -ConnParams $pgParams `
        -DatabaseName $pgParams.Database `
        -Mode $effectivePgToolMode `
        -DockerImage $script:S55PgDockerImage

    if ($null -eq $tableRowCounts -or $tableRowCounts.Count -eq 0) {
        throw "[ABORT] Du lieu baseline row counts rong hoac khong hop le truoc khi ghi manifest."
    }
    Write-Host "[BASELINE] Da thu thap row counts thanh cong cho $($tableRowCounts.Count) bang (fail-closed)."

    # 6. Ghi Manifest JSON tạm (Version 2.0)
    $manifestData = [PSCustomObject]@{
        backupId             = $backupId
        manifestVersion      = "2.0"
        createdAtUtc         = (Get-Date).ToUniversalTime().ToString("o")
        backupFile           = $finalDumpFileName
        encryptedFile        = $null
        sizeBytes            = $dumpItem.Length
        encryptedSizeBytes   = $null
        plaintextSha256      = $sha256
        sha256               = $sha256
        encryptedSha256      = $null
        encryption           = $null
        sourceHost           = $pgParams.Host
        sourceDatabase       = $pgParams.Database
        pgDumpVersion        = $pgDumpVersion
        format               = "PostgreSql_CustomFormat_Fc"
        status               = "VerifiedValid"
        offsiteStatus        = if ($EnableOffsite) { "Pending" } else { "LocalOnly" }
        offsiteVerifiedAtUtc = $null
        tableRowCounts       = $tableRowCounts
    }

    $manifestData | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $stagingManifestPath -Encoding utf8

    # 7. CÔNG BỐ ATOMIC: Chỉ đổi tên thành file chính thức khi toàn bộ kiểm tra ĐÃ ĐẠT
    Move-Item -LiteralPath $stagingDumpFilePath -Destination $finalDumpFilePath -Force
    Move-Item -LiteralPath $stagingManifestPath -Destination $finalManifestPath -Force

    $sizeMb = [math]::Round($dumpItem.Length / 1MB, 2)
    Write-Host "------------------------------------------------------------------"
    Write-Host "[THANH CONG] Ban backup da duoc tao va xac minh hop le!"
    Write-Host " - File: $finalDumpFileName"
    Write-Host (" - Kich thuoc: {0} MB ({1} bytes)" -f $sizeMb, $dumpItem.Length)
    Write-Host " - SHA-256: $sha256"
    Write-Host " - Manifest: $finalManifestPath"
    Write-Host "------------------------------------------------------------------"

    # 7.1. NẾU BẬT OFFSITE: MÃ HÓA BẰNG AGE VÀ UPLOAD R2
    if ($EnableOffsite) {
        Write-Host "------------------------------------------------------------------"
        Write-Host "[OFFSITE] Khoi chay quy trinh ma hoa age va upload Cloudflare R2..."
        Write-Host "------------------------------------------------------------------"

        $uploadScriptPath = Join-Path $PSScriptRoot "backup-upload-r2.ps1"
        if (-not (Test-Path -LiteralPath $uploadScriptPath)) {
            throw "[LOI MOI TRUONG] Khong tim thay scripts/backup-upload-r2.ps1 de thuc hien offsite backup."
        }
        . $uploadScriptPath

        $effectiveAgeKey = $AgeRecipientKey
        if ([string]::IsNullOrWhiteSpace($effectiveAgeKey) -and -not [string]::IsNullOrWhiteSpace($env:AGE_RECIPIENT_PUBKEY)) {
            $effectiveAgeKey = $env:AGE_RECIPIENT_PUBKEY
        }

        $finalEncryptedFileName = "$finalDumpFileName.age"
        $finalEncryptedFilePath = Join-Path $BackupDir $finalEncryptedFileName

        $encResult = Invoke-AgeEncryption -InputDumpPath $finalDumpFilePath `
                                         -OutputEncryptedPath $finalEncryptedFilePath `
                                         -RecipientKey $effectiveAgeKey `
                                         -AgeExePath $AgeExePath `
                                         -MockCommandBlock $OffsiteMockCommandBlock

        # Cập nhật manifest với thông tin file mã hóa
        $manifestData.encryptedFile = $finalEncryptedFileName
        $manifestData.encryptedSizeBytes = $encResult.EncryptedBytes
        $manifestData.encryptedSha256 = $encResult.EncryptedSha256
        $manifestData.encryption = [PSCustomObject]@{
            algorithm = "age-x25519"
            recipient = $effectiveAgeKey
        }
        $manifestData.format = "PostgreSql_CustomFormat_Fc_Age"
        $manifestData | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $finalManifestPath -Encoding utf8

        # Cấu hình R2
        $effectiveR2Config = @{
            AccountId       = if (-not [string]::IsNullOrWhiteSpace($R2AccountId)) { $R2AccountId } else { $env:R2_ACCOUNT_ID }
            BucketName      = if (-not [string]::IsNullOrWhiteSpace($R2BucketName)) { $R2BucketName } else { $env:R2_BUCKET_NAME }
            AccessKeyId     = if (-not [string]::IsNullOrWhiteSpace($R2AccessKeyId)) { $R2AccessKeyId } else { $env:R2_ACCESS_KEY_ID }
            SecretAccessKey = if (-not [string]::IsNullOrWhiteSpace($R2SecretAccessKey)) { $R2SecretAccessKey } else { $env:R2_SECRET_ACCESS_KEY }
            Prefix          = Resolve-S55R2Prefix -R2Prefix $R2Prefix
        }

        $uploadResult = Invoke-R2BackupUpload -EncryptedFilePath $finalEncryptedFilePath `
                                              -ManifestFilePath $finalManifestPath `
                                              -R2Config $effectiveR2Config `
                                              -RcloneExePath $RcloneExePath `
                                              -MockCommandBlock $OffsiteMockCommandBlock

        Write-Host "[OFFSITE] Upload va xac minh remote hoan tat: $($uploadResult.OffsiteStatus)"

        # Thực thi retention trên R2 — sau khi upload đã VerifiedUploaded (F5):
        # lỗi retention transient chỉ cảnh báo, không biến backup thành "thất bại".
        $remoteRetentionResult = Invoke-S55RemoteRetentionSafe -R2Config $effectiveR2Config `
                                                               -RetentionLimit $MaxRetention `
                                                               -RcloneExePath $RcloneExePath `
                                                               -MockCommandBlock $OffsiteMockCommandBlock
        Write-Host "[OFFSITE RETENTION] Ket qua retention tren R2: $($remoteRetentionResult.Status) (Giu: $($remoteRetentionResult.RetainedCount), Xoa: $($remoteRetentionResult.DeletedCount))"
    }

    $backupSuccess = $true

} catch {
    # -ErrorAction Continue de KHONG lam gian doan cleanup/retention phia sau
    # (mac dinh Stop se nem loi ngay trong khoi catch va bo qua don dep file staging)
    Write-Error "[BACKUP THAT BAI] $_" -ErrorAction Continue
    # Dọn dẹp file staging dở dang khi thất bại để không tạo file rác
    if (Test-Path -LiteralPath $stagingDumpFilePath) {
        Write-Warning "[CLEANUP] Dang xoa file dump dở dang do that bai: $stagingDumpFileName"
        Remove-Item -LiteralPath $stagingDumpFilePath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $stagingManifestPath) {
        Remove-Item -LiteralPath $stagingManifestPath -Force -ErrorAction SilentlyContinue
    }
    $backupSuccess = $false
} finally {
    if ($null -ne $oldPgPassword) {
        $env:PGPASSWORD = $oldPgPassword
    } else {
        Remove-Item env:PGPASSWORD -ErrorAction SilentlyContinue
    }
    Remove-Item env:PGSSLMODE -ErrorAction SilentlyContinue
    Remove-Item env:PGSSLROOTCERT -ErrorAction SilentlyContinue
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch { }
    }
}

# 8. Thực thi chính sách Retention 14 bản
Write-Host "[RETENTION] Dang kiem tra chinh sach luu tru toi da $MaxRetention ban..."
$retentionResult = Invoke-BackupRetention -DirectoryPath $BackupDir -RetentionLimit $MaxRetention -IsCurrentBackupFailed (-not $backupSuccess)

if (-not $backupSuccess) {
    exit 1
}

Write-Host "[HOAN TAT] Tac vu sao luu S-55 ket thuc thanh cong."
exit 0
