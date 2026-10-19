<#
.SYNOPSIS
    S-55 Phase 3: Module ma hoa client-side bang age va dong bo offsite Cloudflare R2 (S3-compatible).

.DESCRIPTION
    - Ma hoa client-side bat doi xung bang age: Su dung public key (age1...) tren may chu backup; private key khong luu tren server.
    - Xac minh tinh toan ven 2 lop: Luu ca plaintextSha256 (cho restore drill) va encryptedSha256 (cho offsite upload/download).
    - Manifest Version 2.0: Luu Backup ID, thoi gian tao ISO 8601 co mui gio, ten file, dual hashes, thong so ma hoa, trang thai noi bo va offsite.
    - Upload an toan len Cloudflare R2 qua rclone (S3-compatible API).
    - Khong truyen credentials qua tham so dong lenh (truyen qua bien moi truong phien).
    - Xac minh remote bang cach tai ve thu muc tam va so sanh SHA-256 (khong dua vao ETag).
    - Chinh sach Retention offsite doc lap: Giu 14 ban hop le gan nhat; chi xoa sau khi ban moi da duoc xac minh 100%.
    - Fail-closed: Dung va bao ve toan bo backup cu neu ma hoa, upload hoac xac minh that bai.
    - Ho tro che do Mock/Dry-run de kiem thu offline 100% tren clone sach ma khong can tai khoan cloud that.
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$DumpFile,

    [Parameter()]
    [string]$ManifestFile,

    [Parameter()]
    [string]$AgeRecipientKey,

    [Parameter()]
    [string]$AgeExePath,

    [Parameter()]
    [string]$RcloneExePath,

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
    [int]$MaxRetention = 14,

    [Parameter()]
    [switch]$TestOnlyValidation,

    [Parameter()]
    [hashtable]$TestValidationInputs,

    [Parameter()]
    [scriptblock]$MockCommandBlock
)

$ErrorActionPreference = 'Stop'

# Nap ham quoting tham so CLI dung chuan MSVCRT tu s55-common.ps1 (neu chua co)
if (-not (Get-Command ConvertTo-S55CliArgumentLine -ErrorAction SilentlyContinue)) {
    $scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
    if (-not [string]::IsNullOrWhiteSpace($scriptDir)) {
        $commonScriptPath = Join-Path $scriptDir "s55-common.ps1"
        if (Test-Path -LiteralPath $commonScriptPath) {
            . $commonScriptPath
        }
    }
}

# ==============================================================================
# 1. HÀM XÁC THỰC CẤU HÌNH CLOUDFLARE R2
# ==============================================================================
function Validate-R2Config {
    param(
        [hashtable]$Config
    )

    if ($null -eq $Config) {
        throw "[LOI CAU HINH R2] Cau hinh R2 khong duoc de trong."
    }

    $requiredFields = @("AccountId", "BucketName", "AccessKeyId", "SecretAccessKey")
    foreach ($field in $requiredFields) {
        if (-not $Config.ContainsKey($field) -or [string]::IsNullOrWhiteSpace($Config[$field])) {
            throw "[LOI CAU HINH R2] Thieu bien cau hinh bat buoc: '$field'. Khong duoc de trong hoac null."
        }
    }

    # Kiem tra dinh dang Account ID (32 ky tu hex hoac alphanumeric)
    $accountId = $Config.AccountId.Trim()
    if ($accountId -match '\s' -or $accountId.Length -lt 8) {
        throw "[LOI CAU HINH R2] R2 Account ID khong hop le (chua ky tu trang hoac qua ngan)."
    }

    # Kiem tra dinh dang Bucket Name (chuan S3: chu thuong, so, dau gach ngang, 3-63 ky tu)
    $bucketName = $Config.BucketName.Trim()
    if (-not ($bucketName -cmatch '^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$')) {
        throw "[LOI CAU HINH R2] Ten bucket '$bucketName' khong hop le theo chuan S3 (3-63 ky tu, chi gom chu thuong, so va dau gach ngang, khong bat dau/ket thuc bang gach ngang)."
    }

    # Kiem tra AccessKeyId va SecretAccessKey khong chua ky tu trang
    if ($Config.AccessKeyId.Trim() -match '\s') {
        throw "[LOI CAU HINH R2] AccessKeyId khong duoc chua khoang trang."
    }
    if ($Config.SecretAccessKey.Trim() -match '\s') {
        throw "[LOI CAU HINH R2] SecretAccessKey khong duoc chua khoang trang."
    }

    # Thu tu uu tien prefix: Config.Prefix -> env R2_PREFIX -> mac dinh 'database-backups'
    $effectivePrefix = $Config.Prefix
    if ([string]::IsNullOrWhiteSpace($effectivePrefix)) { $effectivePrefix = $env:R2_PREFIX }
    if ([string]::IsNullOrWhiteSpace($effectivePrefix)) { $effectivePrefix = "database-backups" }

    return @{
        AccountId       = $accountId
        BucketName      = $bucketName
        AccessKeyId     = $Config.AccessKeyId.Trim()
        SecretAccessKey = $Config.SecretAccessKey.Trim()
        Prefix          = $effectivePrefix.Trim().Trim('/')
        Endpoint        = if (-not [string]::IsNullOrWhiteSpace($Config.Endpoint)) { $Config.Endpoint.Trim() } else { "https://$accountId.r2.cloudflarestorage.com" }
    }
}

# ==============================================================================
# 2. HÀM XÁC THỰC AGE RECIPIENT PUBLIC KEY
# ==============================================================================
function Validate-AgeRecipientKey {
    param(
        [Parameter(Mandatory=$false)]
        [string]$RecipientKey
    )

    if ([string]::IsNullOrWhiteSpace($RecipientKey)) {
        throw "[LOI MA HOA AGE] Age Recipient Key khong duoc de trong. Phai cung cap public key bat dau bang 'age1...'."
    }

    $trimmed = $RecipientKey.Trim()

    # Tu choi tuyet doi neu nhan nham private key (AGE-SECRET-KEY-1...)
    if ($trimmed.StartsWith("AGE-SECRET-KEY-1")) {
        throw "[LOI AN TOAN] Khoa duoc cung cap la PRIVATE KEY! Tuyet doi khong dua private key vao may chu backup hoac script tao backup."
    }

    # Public key cua age theo chuan Bech32 bat dau bang 'age1' va co do dai khoang 62 ky tu
    if (-not ($trimmed -match '^age1[0-9a-z]{58}$')) {
        throw "[LOI MA HOA AGE] Age Recipient Key khong dung dinh dang Bech32 X25519 public key (Phai bat dau bang 'age1' va dai 62 ky tu). Gia tri nhan duoc: length=$($trimmed.Length)."
    }

    return $trimmed
}

# ==============================================================================
# 3. HÀM TÌM CÔNG CỤ (age & rclone)
# ==============================================================================
function Get-ToolExecutable {
    param(
        [string]$ToolName,
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath) -and (Test-Path -LiteralPath $ExplicitPath)) {
        return (Resolve-Path $ExplicitPath).Path
    }

    $inPath = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($null -ne $inPath) {
        return $inPath.Source
    }

    $standardLocations = @(
        "C:\Program Files\$ToolName\$ToolName.exe",
        "C:\tools\$ToolName\$ToolName.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\$ToolName\$ToolName.exe"),
        (Join-Path $env:USERPROFILE "scoop\shims\$ToolName.exe"),
        (Join-Path $env:ProgramData "chocolatey\bin\$ToolName.exe")
    )

    foreach ($loc in $standardLocations) {
        if (Test-Path -LiteralPath $loc) {
            return $loc
        }
    }

    return $null
}

# ==============================================================================
# 4. HÀM MÃ HÓA BẰNG AGE (CLIENT-SIDE ENCRYPTION CÓ STAGING AN TOÀN)
# ==============================================================================
function Invoke-AgeEncryption {
    param(
        [Parameter(Mandatory)]
        [string]$InputDumpPath,

        [Parameter(Mandatory)]
        [string]$OutputEncryptedPath,

        [Parameter(Mandatory)]
        [string]$RecipientKey,

        [Parameter()]
        [string]$AgeExePath,

        [Parameter()]
        [scriptblock]$MockCommandBlock
    )

    # 1. Kiem tra file dau vao
    if (-not (Test-Path -LiteralPath $InputDumpPath)) {
        throw "[LOI MA HOA AGE] File dump dau vao khong ton tai: $InputDumpPath"
    }

    $dumpItem = Get-Item -LiteralPath $InputDumpPath
    if ($dumpItem.Length -le 1024) {
        throw "[LOI MA HOA AGE] File dump dau vao qua nho ($($dumpItem.Length) bytes), nghi ngo dump loi hoac rong."
    }

    # 2. Xac thuc public key
    $validKey = Validate-AgeRecipientKey -RecipientKey $RecipientKey

    # 3. Tinh SHA-256 cua dump goc truoc khi ma hoa
    $plaintextSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $InputDumpPath).Hash

    # 4. Su dung staging file de ma hoa an toan
    $stagingEncryptedPath = "$OutputEncryptedPath.in_progress"
    if (Test-Path -LiteralPath $stagingEncryptedPath) {
        Remove-Item -LiteralPath $stagingEncryptedPath -Force -ErrorAction SilentlyContinue
    }

    Write-Host "[ENCRYPT] Dang ma hoa file dump bang age (Recipient: $($validKey.Substring(0, 10))...$($validKey.Substring($validKey.Length - 6)))..."

    $encryptionSuccess = $false
    try {
        if ($null -ne $MockCommandBlock) {
            # Che do mock offline
            $mockArgs = @("-r", $validKey, "-o", $stagingEncryptedPath, $InputDumpPath)
            $mockResult = & $MockCommandBlock -Command "age" -Arguments $mockArgs -ArgsList $mockArgs -InputPath $InputDumpPath -OutputPath $stagingEncryptedPath
            if ($null -ne $mockResult -and $mockResult.ContainsKey("ExitCode") -and $mockResult.ExitCode -ne 0) {
                throw "age mock process tra ve exit code loi: $($mockResult.ExitCode)"
            }
        } else {
            $resolvedAge = Get-ToolExecutable -ToolName "age" -ExplicitPath $AgeExePath
            if (-not $resolvedAge) {
                throw "[LOI MOI TRUONG] Khong tim thay cong cu 'age' tren he thong. Vui long cai dat age (https://github.com/FiloSottile/age) hoac truyen qua -AgeExePath."
            }

            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $resolvedAge
            $psi.Arguments = "-r `"$validKey`" -o `"$stagingEncryptedPath`" `"$InputDumpPath`""
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true

            $proc = [System.Diagnostics.Process]::Start($psi)
            $finished = $proc.WaitForExit(300000) # Timeout 5 phut
            if (-not $finished) {
                $proc.Kill()
                throw "[TIMEOUT] Qua trinh ma hoa age vuot qua 300 giay va da bi dung an toan."
            }
            if ($proc.ExitCode -ne 0) {
                throw "Tien trinh age that bai voi ma thoat: $($proc.ExitCode)"
            }
        }

        # Kiem tra file ma hoa dau ra trong staging
        if (-not (Test-Path -LiteralPath $stagingEncryptedPath)) {
            throw "[LOI MA HOA AGE] File ma hoa staging khong duoc tao sau khi age hoan thanh."
        }

        $encItem = Get-Item -LiteralPath $stagingEncryptedPath
        if ($encItem.Length -le 64) {
            throw "[LOI MA HOA AGE] File ma hoa co kich thuoc qua nho ($($encItem.Length) bytes), nghi ngo ma hoa bi loi hoac file rong."
        }

        # Tinh SHA-256 cua file ma hoa staging
        $encryptedSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $stagingEncryptedPath).Hash

        # Atomic Move sang file chinh thuc
        Move-Item -LiteralPath $stagingEncryptedPath -Destination $OutputEncryptedPath -Force
        $encryptionSuccess = $true

        Write-Host "[ENCRYPT] Ma hoa hoan tat thanh cong!"
        Write-Host " - File ma hoa : $(Split-Path $OutputEncryptedPath -Leaf)"
        Write-Host " - Plaintext SHA-256: $plaintextSha256"
        Write-Host " - Encrypted SHA-256: $encryptedSha256"

        return @{
            PlaintextSha256 = $plaintextSha256
            EncryptedSha256 = $encryptedSha256
            EncryptedPath   = $OutputEncryptedPath
            EncryptedBytes  = $encItem.Length
            Status          = "EncryptedSuccess"
        }

    } catch {
        # Don dep file staging do dang khi loi de khong de lai rac
        if (Test-Path -LiteralPath $stagingEncryptedPath) {
            Write-Warning "[CLEANUP] Dang xoa file staging ma hoa do dang do loi: $stagingEncryptedPath"
            Remove-Item -LiteralPath $stagingEncryptedPath -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

# ==============================================================================
# 5. HÀM GIẢI MÃ BẰNG AGE (PHỤC VỤ RESTORE DRILL CHO BẢN ENCRYPTED)
# ==============================================================================
function Invoke-AgeDecryption {
    param(
        [Parameter(Mandatory)]
        [string]$EncryptedFilePath,

        [Parameter(Mandatory)]
        [string]$OutputDecryptedPath,

        [Parameter(Mandatory)]
        [string]$IdentityFilePath,

        [Parameter()]
        [string]$AgeExePath,

        [Parameter()]
        [scriptblock]$MockCommandBlock
    )

    if (-not (Test-Path -LiteralPath $EncryptedFilePath)) {
        throw "[LOI GIAI MA AGE] File ma hoa khong ton tai: $EncryptedFilePath"
    }
    if (-not (Test-Path -LiteralPath $IdentityFilePath)) {
        throw "[LOI GIAI MA AGE] File identity private key khong ton tai: $IdentityFilePath"
    }

    $stagingDecrypted = "$OutputDecryptedPath.in_progress"
    if (Test-Path -LiteralPath $stagingDecrypted) {
        Remove-Item -LiteralPath $stagingDecrypted -Force -ErrorAction SilentlyContinue
    }

    try {
        if ($null -ne $MockCommandBlock) {
            $decArgs = @("-d", "-i", $IdentityFilePath, "-o", $stagingDecrypted, $EncryptedFilePath)
            $mockResult = & $MockCommandBlock -Command "age-decrypt" -Arguments $decArgs -ArgsList $decArgs
            if ($null -ne $mockResult -and $mockResult.ContainsKey("ExitCode") -and $mockResult.ExitCode -ne 0) {
                throw "age decryption mock process tra ve exit code: $($mockResult.ExitCode)"
            }
        } else {
            $resolvedAge = Get-ToolExecutable -ToolName "age" -ExplicitPath $AgeExePath
            if (-not $resolvedAge) {
                throw "[LOI MOI TRUONG] Khong tim thay 'age' de giai ma."
            }

            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $resolvedAge
            $psi.Arguments = "-d -i `"$IdentityFilePath`" -o `"$stagingDecrypted`" `"$EncryptedFilePath`""
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true

            $proc = [System.Diagnostics.Process]::Start($psi)
            $finished = $proc.WaitForExit(300000)
            if (-not $finished) {
                $proc.Kill()
                throw "[TIMEOUT] Giai ma age bi timeout sau 300 giay."
            }
            if ($proc.ExitCode -ne 0) {
                throw "Tien trinh age giai ma that bai voi exit code: $($proc.ExitCode)"
            }
        }

        if (-not (Test-Path -LiteralPath $stagingDecrypted)) {
            throw "File giai ma khong ton tai sau khi chay age."
        }

        Move-Item -LiteralPath $stagingDecrypted -Destination $OutputDecryptedPath -Force
        return @{
            DecryptedPath = $OutputDecryptedPath
            Sha256        = (Get-FileHash -Algorithm SHA256 -LiteralPath $OutputDecryptedPath).Hash
            Status        = "DecryptedSuccess"
        }
    } finally {
        if (Test-Path -LiteralPath $stagingDecrypted) {
            Remove-Item -LiteralPath $stagingDecrypted -Force -ErrorAction SilentlyContinue
        }
    }
}

# ==============================================================================
# 6. HÀM TẠO MANIFEST VERSION 2.0 (DUAL HASH & OFFSITE METADATA)
# ==============================================================================
function New-BackupManifestV2 {
    param(
        [Parameter(Mandatory)]
        [string]$BackupId,

        [Parameter(Mandatory)]
        [string]$DumpFileName,

        [Parameter(Mandatory)]
        [string]$EncryptedFileName,

        [Parameter(Mandatory)]
        [string]$PlaintextSha256,

        [Parameter(Mandatory)]
        [string]$EncryptedSha256,

        [Parameter(Mandatory)]
        [long]$PlaintextSizeBytes,

        [Parameter(Mandatory)]
        [long]$EncryptedSizeBytes,

        [Parameter(Mandatory)]
        [string]$AgeRecipientKey,

        [Parameter()]
        [string]$SourceHost = "localhost",

        [Parameter()]
        [string]$SourceDatabase = "event_ticket_db",

        [Parameter()]
        [string]$PgDumpVersion = "PostgreSQL 16.x",

        [Parameter()]
        [string]$OffsiteStatus = "Pending"
    )

    $manifest = [PSCustomObject]@{
        backupId             = $BackupId
        manifestVersion      = "2.0"
        createdAtUtc         = (Get-Date).ToUniversalTime().ToString("o")
        backupFile           = $DumpFileName
        encryptedFile        = $EncryptedFileName
        sizeBytes            = $PlaintextSizeBytes
        encryptedSizeBytes   = $EncryptedSizeBytes
        plaintextSha256      = $PlaintextSha256
        sha256               = $PlaintextSha256 # Tuong thich nguoc voi Version 1.0
        encryptedSha256      = $EncryptedSha256
        encryption           = [PSCustomObject]@{
            algorithm = "age-x25519"
            recipient = $AgeRecipientKey
        }
        sourceHost           = $SourceHost
        sourceDatabase       = $SourceDatabase
        pgDumpVersion        = $PgDumpVersion
        format               = "PostgreSql_CustomFormat_Fc_Age"
        status               = "VerifiedValid"
        offsiteStatus        = $OffsiteStatus
        offsiteVerifiedAtUtc = $null
    }

    return $manifest
}

# ==============================================================================
# 7. HÀM XÁC THỰC TÍNH TOÀN VẸN CỦA MANIFEST V2 (CHỐNG TIN TƯỞNG MÙ QUÁNG)
# ==============================================================================
function Assert-BackupManifestV2Integrity {
    param(
        [Parameter(Mandatory)]
        [string]$ManifestFilePath,

        [Parameter()]
        [string]$ExpectedDumpPath,

        [Parameter()]
        [string]$ExpectedEncryptedPath
    )

    if (-not (Test-Path -LiteralPath $ManifestFilePath)) {
        throw "[LOI MANIFEST] File manifest khong ton tai: $ManifestFilePath"
    }

    $content = $null
    try {
        $content = Get-Content -LiteralPath $ManifestFilePath -Raw | ConvertFrom-Json
    } catch {
        throw "[LOI MANIFEST] Khong the doc hoac parse manifest JSON: $($_.Exception.Message)"
    }

    if ($null -eq $content) {
        throw "[LOI MANIFEST] Manifest rong hoac null."
    }

    # Kiem tra cac truong bat buoc
    if ([string]::IsNullOrWhiteSpace($content.backupId)) {
        throw "[LOI MANIFEST] Manifest thieu truong 'backupId'."
    }
    if ([string]::IsNullOrWhiteSpace($content.backupFile)) {
        throw "[LOI MANIFEST] Manifest thieu truong 'backupFile'."
    }
    if ([string]::IsNullOrWhiteSpace($content.status) -or $content.status -ne "VerifiedValid") {
        throw "[LOI MANIFEST] Trang thai manifest khong phai 'VerifiedValid' (Hien tai: '$($content.status)')."
    }

    # Kiem tra version
    $version = if ($content.PSObject.Properties['manifestVersion']) { $content.manifestVersion } else { "1.0" }

    # Kiem tra Plaintext SHA-256
    $ptSha = if ($content.PSObject.Properties['plaintextSha256']) { $content.plaintextSha256 } else { $content.sha256 }
    if ([string]::IsNullOrWhiteSpace($ptSha)) {
        throw "[LOI MANIFEST] Manifest thieu ma bam SHA-256 cua ban dump goc."
    }

    # Doi soat file dump goc tren dia neu co truyen
    if (-not [string]::IsNullOrWhiteSpace($ExpectedDumpPath) -and (Test-Path -LiteralPath $ExpectedDumpPath)) {
        $dumpItem = Get-Item -LiteralPath $ExpectedDumpPath
        if ($dumpItem.Name -ne $content.backupFile) {
            throw "[LOI MANIFEST] Ten file dump goc tren dia ($($dumpItem.Name)) khong khop voi manifest ($($content.backupFile))."
        }
        $actualPtHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ExpectedDumpPath).Hash
        if ($actualPtHash -ne $ptSha) {
            throw "[LOI MANIFEST] SHA-256 cua file dump thuc te ($actualPtHash) khong khop voi manifest ($ptSha)."
        }
    }

    # Doi soat file ma hoa tren dia neu co truyen
    if ($version -eq "2.0") {
        if ([string]::IsNullOrWhiteSpace($content.encryptedFile)) {
            throw "[LOI MANIFEST] Manifest v2.0 thieu truong 'encryptedFile'."
        }
        if ([string]::IsNullOrWhiteSpace($content.encryptedSha256)) {
            throw "[LOI MANIFEST] Manifest v2.0 thieu truong 'encryptedSha256'."
        }

        if (-not [string]::IsNullOrWhiteSpace($ExpectedEncryptedPath) -and (Test-Path -LiteralPath $ExpectedEncryptedPath)) {
            $encItem = Get-Item -LiteralPath $ExpectedEncryptedPath
            if ($encItem.Name -ne $content.encryptedFile) {
                throw "[LOI MANIFEST] Ten file ma hoa tren dia ($($encItem.Name)) khong khop voi manifest ($($content.encryptedFile))."
            }
            $actualEncHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ExpectedEncryptedPath).Hash
            if ($actualEncHash -ne $content.encryptedSha256) {
                throw "[LOI MANIFEST] SHA-256 cua file ma hoa thuc te ($actualEncHash) khong khop voi manifest ($($content.encryptedSha256))."
            }
        }
    }

    return $content
}

# ==============================================================================
# 8. HÀM THỰC THI RCLONE AN TOÀN (KHÔNG LỘ CREDENTIALS TRÊN DÒNG LỆNH)
# ==============================================================================
function Invoke-RcloneCommand {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [hashtable]$R2Config,

        [Parameter()]
        [string]$RcloneExePath,

        [Parameter()]
        [int]$TimeoutSeconds = 300,

        [Parameter()]
        [scriptblock]$MockCommandBlock
    )

    # Thiet lap cau hinh R2 qua BIEN MOI TRUONG CUA PHIEN (Tuyet doi khong truyen secret tren CLI args).
    # Luu truoc gia tri cu de khoi phuc trong khoi finally (ke ca khi mock).
    $oldType = $env:RCLONE_CONFIG_R2_TYPE
    $oldProvider = $env:RCLONE_CONFIG_R2_PROVIDER
    $oldKeyId = $env:RCLONE_CONFIG_R2_ACCESS_KEY_ID
    $oldSecret = $env:RCLONE_CONFIG_R2_SECRET_ACCESS_KEY
    $oldEndpoint = $env:RCLONE_CONFIG_R2_ENDPOINT
    $oldNoCheckBucket = $env:RCLONE_CONFIG_R2_NO_CHECK_BUCKET

    try {
        $env:RCLONE_CONFIG_R2_TYPE = "s3"
        $env:RCLONE_CONFIG_R2_PROVIDER = "Cloudflare"
        $env:RCLONE_CONFIG_R2_ACCESS_KEY_ID = $R2Config.AccessKeyId
        $env:RCLONE_CONFIG_R2_SECRET_ACCESS_KEY = $R2Config.SecretAccessKey
        $env:RCLONE_CONFIG_R2_ENDPOINT = $R2Config.Endpoint
        # FIX (403 AccessDenied / CreateBucket): Cloudflare R2 KHONG ho tro tao
        # bucket qua S3 API (chi tao tren Dashboard). Khi thieu no_check_bucket,
        # rclone HEAD bucket truoc moi lan upload; neu HEAD tra 403/404 (API
        # token chi co quyen object-level) rclone co goi CreateBucket va nhan
        # "failed to prepare upload: operation error S3: CreateBucket, https
        # response error StatusCode: 403, api error AccessDenied". Bucket da ton
        # tai san nen bo qua buoc kiem tra/tao bucket la cach chinh thuc cua
        # rclone cho R2 (https://rclone.org/s3/#standard-options, no_check_bucket).
        # Khong anh huong read path (lsf/lsjson) va khong lam giam muc xac minh
        # toan ven: sau upload van readback + so sanh SHA-256.
        $env:RCLONE_CONFIG_R2_NO_CHECK_BUCKET = "true"

        if ($null -ne $MockCommandBlock) {
            # Che do Mock: Chuyen cau hinh va tham so toi block kiem thu
            # (env da duoc thiet lap de test co the xac minh anh xa cau hinh).
            return (& $MockCommandBlock -Command "rclone" -Arguments $Arguments -ArgsList $Arguments -R2Config $R2Config)
        }

        $resolvedRclone = Get-ToolExecutable -ToolName "rclone" -ExplicitPath $RcloneExePath
        if (-not $resolvedRclone) {
            throw "[LOI MOI TRUONG] Khong tim thay 'rclone' tren he thong. Vui long cai dat rclone (https://rclone.org) hoac truyen qua -RcloneExePath."
        }

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $resolvedRclone
        # Quoting chuan MSVCRT / CommandLineToArgvW tu s55-common.ps1
        if (Get-Command ConvertTo-S55CliArgumentLine -ErrorAction SilentlyContinue) {
            $psi.Arguments = ConvertTo-S55CliArgumentLine -Arguments $Arguments
        } else {
            $psi.Arguments = ($Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
        }
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true

        $process = $null
        try {
            $process = [System.Diagnostics.Process]::Start($psi)
        } catch {
            $startErr = $_.Exception.Message -replace [regex]::Escape($R2Config.SecretAccessKey), '***' -replace [regex]::Escape($R2Config.AccessKeyId), '***'
            throw "Khong the khoi chay rclone ('$resolvedRclone'): $startErr"
        }

        # Doc bat dong bo ca stdout lan stderr de tranh deadlock khi pipe buffer bi day
        $stdOutTask = $process.StandardOutput.ReadToEndAsync()
        $stdErrTask = $process.StandardError.ReadToEndAsync()

        $finished = $process.WaitForExit($TimeoutSeconds * 1000)
        if (-not $finished) {
            try {
                $process.Kill($true)
            } catch {
                try { $process.Kill() } catch { }
            }
            $process.WaitForExit(10000) | Out-Null

            $partialOut = if ($stdOutTask.IsCompleted) { $stdOutTask.Result } else { "" }
            $partialErr = if ($stdErrTask.IsCompleted) { $stdErrTask.Result } else { "" }
            $safeErr = ($partialOut + "`n" + $partialErr) -replace [regex]::Escape($R2Config.SecretAccessKey), '***' -replace [regex]::Escape($R2Config.AccessKeyId), '***'
            throw "[TIMEOUT] Lenh rclone vuot qua $TimeoutSeconds giay va da bi dung an toan (tien trinh da bi kill). Chi tiet loi/output: $safeErr"
        }

        $stdOut = $stdOutTask.Result
        $stdErr = $stdErrTask.Result

        # Che mat bat ky chuoi secret nao co the xuat hien trong output / error
        $safeErr = $stdErr -replace [regex]::Escape($R2Config.SecretAccessKey), '***' -replace [regex]::Escape($R2Config.AccessKeyId), '***'
        $safeOut = $stdOut -replace [regex]::Escape($R2Config.SecretAccessKey), '***' -replace [regex]::Escape($R2Config.AccessKeyId), '***'

        if ($process.ExitCode -ne 0) {
            throw "rclone that bai voi ma thoat $($process.ExitCode): $safeErr"
        }

        return @{
            ExitCode = $process.ExitCode
            Output   = $safeOut
            Error    = $safeErr
        }
    } finally {
        # Don dep ngay bien moi truong nhay cam
        if ($null -ne $oldType) { $env:RCLONE_CONFIG_R2_TYPE = $oldType } else { Remove-Item env:RCLONE_CONFIG_R2_TYPE -ErrorAction SilentlyContinue }
        if ($null -ne $oldProvider) { $env:RCLONE_CONFIG_R2_PROVIDER = $oldProvider } else { Remove-Item env:RCLONE_CONFIG_R2_PROVIDER -ErrorAction SilentlyContinue }
        if ($null -ne $oldKeyId) { $env:RCLONE_CONFIG_R2_ACCESS_KEY_ID = $oldKeyId } else { Remove-Item env:RCLONE_CONFIG_R2_ACCESS_KEY_ID -ErrorAction SilentlyContinue }
        if ($null -ne $oldSecret) { $env:RCLONE_CONFIG_R2_SECRET_ACCESS_KEY = $oldSecret } else { Remove-Item env:RCLONE_CONFIG_R2_SECRET_ACCESS_KEY -ErrorAction SilentlyContinue }
        if ($null -ne $oldEndpoint) { $env:RCLONE_CONFIG_R2_ENDPOINT = $oldEndpoint } else { Remove-Item env:RCLONE_CONFIG_R2_ENDPOINT -ErrorAction SilentlyContinue }
        if ($null -ne $oldNoCheckBucket) { $env:RCLONE_CONFIG_R2_NO_CHECK_BUCKET = $oldNoCheckBucket } else { Remove-Item env:RCLONE_CONFIG_R2_NO_CHECK_BUCKET -ErrorAction SilentlyContinue }
    }
}

# ==============================================================================
# 9. HÀM UPLOAD VÀ XÁC MINH TOÀN VẸN R2 (XÁC MINH TẢI VỀ & SO SÁNH SHA-256)
# ==============================================================================
function Invoke-R2BackupUpload {
    param(
        [Parameter(Mandatory)]
        [string]$EncryptedFilePath,

        [Parameter(Mandatory)]
        [string]$ManifestFilePath,

        [Parameter(Mandatory)]
        [hashtable]$R2Config,

        [Parameter()]
        [string]$RcloneExePath,

        [Parameter()]
        [int]$TimeoutSeconds = 300,

        [Parameter()]
        [scriptblock]$MockCommandBlock
    )

    # 1. Kiem tra file local truoc khi upload
    if (-not (Test-Path -LiteralPath $EncryptedFilePath)) {
        throw "[LOI UPLOAD R2] File ma hoa khong ton tai: $EncryptedFilePath"
    }
    if (-not (Test-Path -LiteralPath $ManifestFilePath)) {
        throw "[LOI UPLOAD R2] File manifest khong ton tai: $ManifestFilePath"
    }

    # 2. Xac thuc manifest
    $manifestData = Assert-BackupManifestV2Integrity -ManifestFilePath $ManifestFilePath -ExpectedEncryptedPath $EncryptedFilePath
    $encFileName = Split-Path $EncryptedFilePath -Leaf
    $manifestFileName = Split-Path $ManifestFilePath -Leaf

    $validatedConfig = Validate-R2Config -Config $R2Config
    $bucket = $validatedConfig.BucketName
    $prefix = $validatedConfig.Prefix

    $remotePathPrefix = if ([string]::IsNullOrWhiteSpace($prefix)) { "R2:$bucket" } else { "R2:$bucket/$prefix" }
    $remoteEncPath = "$remotePathPrefix/$encFileName"
    $remoteManifestPath = "$remotePathPrefix/$manifestFileName"

    Write-Host "[R2 UPLOAD] Bat dau upload ban backup offsite len Cloudflare R2..."
    Write-Host " - Remote Target: $remoteEncPath"

    # Step 1: Upload file ma hoa
    $uploadDumpArgs = @("copyto", $EncryptedFilePath, $remoteEncPath)
    $uploadDumpResult = Invoke-RcloneCommand -Arguments $uploadDumpArgs -R2Config $validatedConfig -RcloneExePath $RcloneExePath -TimeoutSeconds $TimeoutSeconds -MockCommandBlock $MockCommandBlock

    # Step 2: Upload file manifest
    $uploadManifestArgs = @("copyto", $ManifestFilePath, $remoteManifestPath)
    $uploadManifestResult = Invoke-RcloneCommand -Arguments $uploadManifestArgs -R2Config $validatedConfig -RcloneExePath $RcloneExePath -TimeoutSeconds $TimeoutSeconds -MockCommandBlock $MockCommandBlock

    # Step 3: XAC MINH TINH TOAN VEN TREN REMOTE (DOWNLOAD VA SO SANH SHA-256)
    Write-Host "[R2 VERIFY] Dang xac minh tinh toan ven file tren remote qua co che checksum doc lap..."
    $verifyTempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_r2_verify_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $verifyTempDir -Force | Out-Null

    try {
        $tempDownloadEnc = Join-Path $verifyTempDir $encFileName
        $tempDownloadManifest = Join-Path $verifyTempDir $manifestFileName

        # Tai nguoc file ma hoa ve thu muc tam de kiem tra
        $dlDumpArgs = @("copyto", $remoteEncPath, $tempDownloadEnc)
        Invoke-RcloneCommand -Arguments $dlDumpArgs -R2Config $validatedConfig -RcloneExePath $RcloneExePath -TimeoutSeconds $TimeoutSeconds -MockCommandBlock $MockCommandBlock | Out-Null

        if (-not (Test-Path -LiteralPath $tempDownloadEnc)) {
            throw "[LOI XAC MINH R2] Khong the tai file kiem thu tu remote ve: $tempDownloadEnc"
        }

        # Tinh SHA-256 cua file vua tai ve tu remote
        $downloadedSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $tempDownloadEnc).Hash

        if ($downloadedSha256 -ne $manifestData.encryptedSha256) {
            throw "[LOI XAC MINH R2] SHA-256 file tai ve tu remote ($downloadedSha256) KHONG KHOP voi local ($($manifestData.encryptedSha256))! Du lieu remote bi hong hoac truyen loi."
        }

        # Tai nguoc file manifest ve kiem tra
        $dlManifestArgs = @("copyto", $remoteManifestPath, $tempDownloadManifest)
        Invoke-RcloneCommand -Arguments $dlManifestArgs -R2Config $validatedConfig -RcloneExePath $RcloneExePath -TimeoutSeconds $TimeoutSeconds -MockCommandBlock $MockCommandBlock | Out-Null

        if (-not (Test-Path -LiteralPath $tempDownloadManifest)) {
            throw "[LOI XAC MINH R2] Khong the tai file manifest kiem thu tu remote ve: $tempDownloadManifest"
        }

        $dlManifestRaw = Get-Content -LiteralPath $tempDownloadManifest -Raw
        $dlManifestContent = $null
        try {
            $dlManifestContent = $dlManifestRaw | ConvertFrom-Json
        } catch {
            throw "[LOI XAC MINH R2] File manifest tren remote khong phai JSON hop le: $($_.Exception.Message)"
        }

        if ($null -eq $dlManifestContent) {
            throw "[LOI XAC MINH R2] Manifest tren remote rong hoac khong hop le."
        }

        if ($dlManifestContent.backupId -ne $manifestData.backupId) {
            throw "[LOI XAC MINH R2] Manifest tren remote khong khop backupId (remote: '$($dlManifestContent.backupId)', local: '$($manifestData.backupId)')."
        }

        # Kiem tra encryptedSha256 tren remote manifest
        if ([string]::IsNullOrWhiteSpace($dlManifestContent.encryptedSha256) -or $dlManifestContent.encryptedSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "[LOI XAC MINH R2] Manifest tren remote co encryptedSha256 khong dung dinh dang 64 ky tu hex."
        }
        if ($dlManifestContent.encryptedSha256 -ne $manifestData.encryptedSha256) {
            throw "[LOI XAC MINH R2] Manifest tren remote co encryptedSha256 ($($dlManifestContent.encryptedSha256)) KHONG KHOP voi local ($($manifestData.encryptedSha256))."
        }

        Write-Host "[R2 VERIFY] Xac minh toan ven remote hoan tat: SHA-256 KHOP 100% ($downloadedSha256)."

        # Step 4: Cap nhat trang thai manifest offsiteStatus = 'VerifiedUploaded'
        if (-not $manifestData.PSObject.Properties['offsiteStatus']) {
            $manifestData | Add-Member -NotePropertyName 'offsiteStatus' -NotePropertyValue 'Pending'
        }
        $manifestData.offsiteStatus = "VerifiedUploaded"

        if (-not $manifestData.PSObject.Properties['offsiteVerifiedAtUtc']) {
            $manifestData | Add-Member -NotePropertyName 'offsiteVerifiedAtUtc' -NotePropertyValue $null
        }
        $manifestData.offsiteVerifiedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        $manifestData | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ManifestFilePath -Encoding utf8

        # Dong bo lai manifest da danh dau thanh cong len remote
        Invoke-RcloneCommand -Arguments @("copyto", $ManifestFilePath, $remoteManifestPath) -R2Config $validatedConfig -RcloneExePath $RcloneExePath -TimeoutSeconds $TimeoutSeconds -MockCommandBlock $MockCommandBlock | Out-Null

        # Step 5: DOC LAI MANIFEST TREN REMOTE DE XAC MINH DA THUC SU CAP NHAT 'VerifiedUploaded'
        $tempReadbackManifest = Join-Path $verifyTempDir ("readback_" + $manifestFileName)
        Invoke-RcloneCommand -Arguments @("copyto", $remoteManifestPath, $tempReadbackManifest) -R2Config $validatedConfig -RcloneExePath $RcloneExePath -TimeoutSeconds $TimeoutSeconds -MockCommandBlock $MockCommandBlock | Out-Null

        if (-not (Test-Path -LiteralPath $tempReadbackManifest)) {
            throw "[LOI XAC MINH R2] Khong the tai lai manifest tu remote sau khi cap nhat VerifiedUploaded."
        }

        $readbackRaw = Get-Content -LiteralPath $tempReadbackManifest -Raw
        $readbackContent = $null
        try {
            $readbackContent = $readbackRaw | ConvertFrom-Json
        } catch {
            throw "[LOI XAC MINH R2] Manifest doc lai tren remote khong phai JSON hop le: $($_.Exception.Message)"
        }

        if ($null -eq $readbackContent) {
            throw "[LOI XAC MINH R2] Manifest doc lai tren remote rong."
        }

        if ($readbackContent.offsiteStatus -ne "VerifiedUploaded") {
            throw "[LOI XAC MINH R2] Manifest tren remote chua cap nhat trang thai 'VerifiedUploaded' sau upload (trang thai hien tai: '$($readbackContent.offsiteStatus)')."
        }
        if ($readbackContent.backupId -ne $manifestData.backupId) {
            throw "[LOI XAC MINH R2] Manifest doc lai tren remote khong khop backupId."
        }
        if ($readbackContent.encryptedSha256 -ne $manifestData.encryptedSha256) {
            throw "[LOI XAC MINH R2] Manifest doc lai tren remote khong khop encryptedSha256."
        }

        return @{
            Success         = $true
            OffsiteStatus   = "VerifiedUploaded"
            RemoteDump      = $remoteEncPath
            RemoteManifest  = $remoteManifestPath
            EncryptedSha256 = $downloadedSha256
        }

    } finally {
        # Don dep thu muc tam xac minh
        if (Test-Path -LiteralPath $verifyTempDir) {
            Remove-Item -Path $verifyTempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# ==============================================================================
# 10. HÀM THỰC THI CHÍNH SÁCH RETENTION OFFSITE TRÊN R2 (14 BẢN HỢP LỆ)
# ==============================================================================
function Invoke-R2RemoteRetention {
    param(
        [Parameter(Mandatory)]
        [hashtable]$R2Config,

        [Parameter()]
        [int]$RetentionLimit = 14,

        [Parameter()]
        [bool]$IsUploadFailed = $false,

        [Parameter()]
        [string]$RcloneExePath,

        [Parameter()]
        [int]$TimeoutSeconds = 300,

        [Parameter()]
        [scriptblock]$MockCommandBlock
    )

    # NGUYÊN TẮC AN TOÀN TUYỆT ĐỐI: Neu upload hoac xac minh that bai, KHONG XOA BAT KY OBJECT NAO
    if ($IsUploadFailed) {
        Write-Warning "[R2 RETENTION] Upload hoac xac minh ban backup moi that bai! Theo nguyen tac an toan, KHONG xoa bat ky object nao tren Cloudflare R2."
        return @{
            Retained     = @()
            Deleted      = @()
            DeletedCount = 0
            Status       = "AbortedDueToUploadFailure"
        }
    }

    $validatedConfig = Validate-R2Config -Config $R2Config
    $bucket = $validatedConfig.BucketName
    $prefix = $validatedConfig.Prefix
    $remotePathPrefix = if ([string]::IsNullOrWhiteSpace($prefix)) { "R2:$bucket" } else { "R2:$bucket/$prefix" }

    Write-Host "[R2 RETENTION] Dang kiem tra chinh sach retention tren Cloudflare R2 ($remotePathPrefix, Max: $RetentionLimit)..."

    # Lay danh sach objects tu remote
    $lsArgs = @("lsjson", $remotePathPrefix)
    $lsResult = Invoke-RcloneCommand -Arguments $lsArgs -R2Config $validatedConfig -RcloneExePath $RcloneExePath -TimeoutSeconds $TimeoutSeconds -MockCommandBlock $MockCommandBlock

    $remoteObjects = @()
    if ($null -ne $lsResult) {
        if ($lsResult -is [array]) {
            $remoteObjects = $lsResult
        } elseif ($lsResult.ContainsKey("Output") -and -not [string]::IsNullOrWhiteSpace($lsResult.Output)) {
            $remoteObjects = $lsResult.Output | ConvertFrom-Json
        }
    }

    # 1. Chi loc cac object thuoc namespace backup cua ung dung: eventpulse_backup_yyyyMMdd_HHmmss.(dump.age|json)
    # F4: rclone lsjson co the tra ve Name co hoac khong kem prefix (tuy cach goi/phien ban).
    # Chuan hoa bang cach boc prefix neu co, chi chap nhan object nam TRUC TIEP trong
    # prefix (khong co thu muc con) — object trong thu muc con duoc coi la ngoai
    # namespace va khong bao gio bi xoa.
    $backupDumpMap = @{}
    $backupManifestMap = @{}
    $foreignFiles = @()
    $prefixNormalized = $prefix.Trim('/')

    foreach ($obj in $remoteObjects) {
        $objName = if ($obj -is [hashtable]) { $obj['Name'] } else { $obj.Name }
        $objSize = if ($obj -is [hashtable]) { $obj['Size'] } else { $obj.Size }
        $relName = $objName
        if (-not [string]::IsNullOrWhiteSpace($prefixNormalized)) {
            if ($objName.StartsWith($prefixNormalized + '/', [System.StringComparison]::OrdinalIgnoreCase)) {
                $relName = $objName.Substring($prefixNormalized.Length + 1)
            }
        }
        if ($relName -match '^eventpulse_backup_(\d{8}_\d{6})\.dump\.age$') {
            $ts = $Matches[1]
            $backupDumpMap[$ts] = [PSCustomObject]@{ Name = $objName; Size = $objSize }
        } elseif ($relName -match '^eventpulse_backup_(\d{8}_\d{6})\.json$') {
            $ts = $Matches[1]
            $backupManifestMap[$ts] = [PSCustomObject]@{ Name = $objName; Size = $objSize }
        } else {
            # File ngoai namespace -> Khong bao gio duoc xoa
            $foreignFiles += $objName
        }
    }

    # 2. Xac dinh cac cap backup hop le (phai co ca dump.age va manifest json, dung namespace va manifest hop le)
    $validBackups = @()
    foreach ($ts in $backupDumpMap.Keys) {
        if ($backupManifestMap.ContainsKey($ts)) {
            $dumpObj = $backupDumpMap[$ts]
            $manifestObj = $backupManifestMap[$ts]
            # Kiem tra dung luong > 0
            if ($dumpObj.Size -gt 64 -and $manifestObj.Size -gt 0) {
                # Kiem tra tinh toan ven noi dung manifest tren remote qua rclone cat (neu co the truy cap)
                $targetManifestPath = if (-not [string]::IsNullOrWhiteSpace($prefixNormalized) -and $manifestObj.Name.StartsWith($prefixNormalized + '/', [System.StringComparison]::OrdinalIgnoreCase)) {
                    "R2:$bucket/$($manifestObj.Name)"
                } else {
                    "$remotePathPrefix/$($manifestObj.Name)"
                }

                $isValidPair = $true
                $manifestContentRaw = $null
                try {
                    $catRes = Invoke-RcloneCommand -Arguments @("cat", $targetManifestPath) -R2Config $validatedConfig -RcloneExePath $RcloneExePath -TimeoutSeconds $TimeoutSeconds -MockCommandBlock $MockCommandBlock
                    if ($null -ne $catRes) {
                        if ($catRes -is [hashtable] -and $catRes.ContainsKey("Output")) {
                            $manifestContentRaw = $catRes.Output
                        } elseif ($catRes -is [string]) {
                            $manifestContentRaw = $catRes
                        }
                    }
                } catch {
                    Write-Warning "[R2 RETENTION] Khong the doc manifest cho ban ${ts}: $($_.Exception.Message). Cap backup bi bo qua khoi tap hop le de bao toan an toan."
                    $isValidPair = $false
                }

                if ($isValidPair -and -not [string]::IsNullOrWhiteSpace($manifestContentRaw)) {
                    try {
                        $parsedMf = $manifestContentRaw | ConvertFrom-Json
                        if ($null -eq $parsedMf -or
                            [string]::IsNullOrWhiteSpace($parsedMf.backupId) -or
                            $parsedMf.backupId -notmatch '^ep-backup-' -or
                            [string]::IsNullOrWhiteSpace($parsedMf.encryptedSha256) -or
                            $parsedMf.encryptedSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
                            $parsedMf.offsiteStatus -ne "VerifiedUploaded") {
                            Write-Warning "[R2 RETENTION] Manifest cua ban $ts khong hop le hoac chua duoc VerifiedUploaded (backupId: '$($parsedMf.backupId)', offsiteStatus: '$($parsedMf.offsiteStatus)'). Cap backup bi loai khoi tap hop le."
                            $isValidPair = $false
                        }
                    } catch {
                        Write-Warning "[R2 RETENTION] Manifest cua ban $ts bi loi JSON. Cap backup bi loai khoi tap hop le."
                        $isValidPair = $false
                    }
                }

                if ($isValidPair) {
                    $validBackups += [PSCustomObject]@{
                        Timestamp    = $ts
                        DumpName     = $dumpObj.Name
                        ManifestName = $manifestObj.Name
                        DumpSize     = $dumpObj.Size
                        ManifestSize = $manifestObj.Size
                    }
                } else {
                    $foreignFiles += $manifestObj.Name
                }
            }
        }
    }

    # 3. Sap xep theo timestamp giam dan (moi nhat len dau)
    $sortedValid = $validBackups | Sort-Object -Property Timestamp -Descending

    $retained = @()
    $deleted = @()
    $deleteErrors = @()
    $partialFailure = $false

    if ($sortedValid.Count -le $RetentionLimit) {
        $retained = $sortedValid
        Write-Host "[R2 RETENTION] Tong so ban backup offsite hop le: $($sortedValid.Count) <= $RetentionLimit. Khong can xoa."
    } else {
        $retained = $sortedValid | Select-Object -First $RetentionLimit
        $toDelete = $sortedValid | Select-Object -Skip $RetentionLimit

        foreach ($item in $toDelete) {
            Write-Host "[R2 RETENTION] Dang xoa ban backup offsite cu qua han: $($item.DumpName) va $($item.ManifestName)"
            # F4: Dung nguyen duong dan tu lsjson (da bao gom prefix neu rclone tra ve),
            # tranh noi prefix 2 lan khi Name da co prefix.
            if (-not [string]::IsNullOrWhiteSpace($prefixNormalized) -and $item.DumpName.StartsWith($prefixNormalized + '/', [System.StringComparison]::OrdinalIgnoreCase)) {
                $targetDump = "R2:$bucket/$($item.DumpName)"
                $targetManifest = "R2:$bucket/$($item.ManifestName)"
            } else {
                $targetDump = "$remotePathPrefix/$($item.DumpName)"
                $targetManifest = "$remotePathPrefix/$($item.ManifestName)"
            }

            # Thu tu xoa: Xoa dump truoc de giai phong dung luong storage, sau do xoa manifest
            $dumpDeleteSuccess = $false
            try {
                Invoke-RcloneCommand -Arguments @("deletefile", $targetDump) -R2Config $validatedConfig -RcloneExePath $RcloneExePath -TimeoutSeconds $TimeoutSeconds -MockCommandBlock $MockCommandBlock | Out-Null
                $deleted += $item.DumpName
                $dumpDeleteSuccess = $true
            } catch {
                $err = $_.Exception.Message
                $deleteErrors += "Loi xoa file dump '$($item.DumpName)': $err"
                Write-Warning "[R2 RETENTION ERROR] Khong the xoa file dump '$($item.DumpName)': $err. Huy buoc xoa manifest de tranh tao orphan record."
                # Dung ngay lap tuc de tranh gay loi day chuyen
                break
            }

            try {
                Invoke-RcloneCommand -Arguments @("deletefile", $targetManifest) -R2Config $validatedConfig -RcloneExePath $RcloneExePath -TimeoutSeconds $TimeoutSeconds -MockCommandBlock $MockCommandBlock | Out-Null
                $deleted += $item.ManifestName
            } catch {
                $err = $_.Exception.Message
                $partialFailure = $true
                $deleteErrors += "Loi xoa manifest '$($item.ManifestName)': $err"
                Write-Warning "[R2 RETENTION ERROR] Da xoa dump '$($item.DumpName)' thanh cong nhung xoa manifest '$($item.ManifestName)' that bai ($err). Trang thai: PartialFailure (orphan manifest con tren R2)."
                # Dung ngay lap tuc
                break
            }
        }
    }

    if ($deleteErrors.Count -gt 0) {
        $status = if ($partialFailure) { "PartialFailure" } else { "Failed" }
        throw "[R2 RETENTION ERROR] Retention that bai voi trang thai $status. Da xoa $($deleted.Count) object ($($deleted -join ', ')). Chi tiet loi: $($deleteErrors -join '; ')"
    }

    return @{
        TotalValidCount = $sortedValid.Count
        RetainedCount   = $retained.Count
        DeletedCount    = $deleted.Count
        DeletedFiles    = $deleted
        ForeignCount    = $foreignFiles.Count
        Status          = "Success"
    }
}

# ==============================================================================
# 11. XỬ LÝ CHẾ ĐỘ TEST CÔ LẬP NẾU TRUYỀN -TestOnlyValidation
# ==============================================================================
if ($TestOnlyValidation) {
    if ($null -ne $TestValidationInputs) {
        if ($TestValidationInputs.ContainsKey("ValidateConfig")) {
            return Validate-R2Config -Config $TestValidationInputs.ValidateConfig
        }
        if ($TestValidationInputs.ContainsKey("ValidateAgeKey")) {
            return Validate-AgeRecipientKey -RecipientKey $TestValidationInputs.ValidateAgeKey
        }
        if ($TestValidationInputs.ContainsKey("EncryptTest")) {
            $inp = $TestValidationInputs.EncryptTest
            return Invoke-AgeEncryption -InputDumpPath $inp.InputDump -OutputEncryptedPath $inp.OutputEnc -RecipientKey $inp.Key -MockCommandBlock $inp.MockBlock
        }
        if ($TestValidationInputs.ContainsKey("ManifestTest")) {
            $inp = $TestValidationInputs.ManifestTest
            return Assert-BackupManifestV2Integrity -ManifestFilePath $inp.ManifestPath -ExpectedDumpPath $inp.ExpectedDump -ExpectedEncryptedPath $inp.ExpectedEnc
        }
        if ($TestValidationInputs.ContainsKey("UploadTest")) {
            $inp = $TestValidationInputs.UploadTest
            return Invoke-R2BackupUpload -EncryptedFilePath $inp.EncFile -ManifestFilePath $inp.ManifestFile -R2Config $inp.Config -MockCommandBlock $inp.MockBlock
        }
        if ($TestValidationInputs.ContainsKey("RetentionTest")) {
            $inp = $TestValidationInputs.RetentionTest
            return Invoke-R2RemoteRetention -R2Config $inp.Config -RetentionLimit $inp.Limit -IsUploadFailed $inp.IsUploadFailed -MockCommandBlock $inp.MockBlock
        }
    }
    return @{ Status = "TestModeReady" }
}
