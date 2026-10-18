<#
.SYNOPSIS
    S-55: Module dùng chung cho backup, restore drill và offsite upload.

.DESCRIPTION
    - Get-PgToolExecutable: Tìm công cụ PostgreSQL (pg_dump/pg_restore/psql) qua PATH hoặc thư mục chuẩn.
    - Parse-PgConnectionString: Phân tích chuỗi kết nối an toàn (hỗ trợ mật khẩu chứa ký tự đặc biệt).
    - Invoke-PgSqlQuery: Thực thi truy vấn SQL qua psql, không để lộ mật khẩu.
    - Test-S55BackupValidity: Xác minh bản backup hợp lệ (manifest + status + kích thước + SHA-256). Dùng chung cho retention và restore drill.
    - Assert-SafeDrillEnvironment: Phòng vệ môi trường drill — DENY-BY-DEFAULT (H1): chỉ cho phép localhost hoặc host trong allowlist tường minh; cấm tuyệt đối cloud/production.
    - Invoke-ProcessWithTimeout: Chạy tiến trình kèm timeout an toàn (H3): terminate process khi quá hạn và trả trạng thái rõ ràng.
#>

$ErrorActionPreference = 'Stop'

# ==============================================================================
# HÀM BỔ TRỢ: XÁC MINH CÔNG CỤ POSTGRESQL (pg_dump, pg_restore, psql)
# ==============================================================================
function Get-PgToolExecutable {
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

    $standardPaths = @(
        "C:\Program Files\PostgreSQL\17\bin\$ToolName.exe",
        "C:\Program Files\PostgreSQL\16\bin\$ToolName.exe",
        "C:\Program Files\PostgreSQL\15\bin\$ToolName.exe"
    )

    foreach ($path in $standardPaths) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }

    return $null
}

# ==============================================================================
# HÀM BỔ TRỢ: PHÂN TÍCH CHUỖI KẾT NỐI AN TOÀN (XỬ LÝ ĐÚNG MẬT KHẨU CÓ KÝ TỰ ĐẶC BIỆT)
# ==============================================================================
function Parse-PgConnectionString {
    param([string]$RawConnection)

    $params = @{
        Host                  = "localhost"
        Port                  = 5432
        Database              = ""
        Username              = "postgres"
        Password              = ""
        SslMode               = "Prefer"
        TrustServerCertificate = $false
    }

    if ([string]::IsNullOrWhiteSpace($RawConnection)) {
        return $params
    }

    # Parser regex bóc tách từng cặp Key=Value, hỗ trợ giá trị được bao trong nháy kép hoặc nháy đơn
    $regex = '(?i)\s*(?<key>[a-zA-Z0-9_\s]+?)\s*=\s*(?:"(?<val>[^"]*)"|''(?<val>[^'']*)''|(?<val>[^;]*))\s*(?:;|$)'
    $matches = [regex]::Matches($RawConnection, $regex)

    foreach ($m in $matches) {
        $key = $m.Groups['key'].Value.Trim()
        $val = $m.Groups['val'].Value
        if ([string]::IsNullOrEmpty($key)) { continue }

        switch -Regex ($key) {
            '^(Host|Server)$'                { $params.Host = $val }
            '^(Port)$'                       {
                $parsedPort = 0
                if ([int]::TryParse($val, [ref]$parsedPort) -and $parsedPort -gt 0 -and $parsedPort -le 65535) {
                    $params.Port = $parsedPort
                } else {
                    throw "[LOI CAU HINH] Port khong hop le trong connection string: $val"
                }
            }
            '^(Database|DB)$'                { $params.Database = $val }
            '^(Username|User ID|User|UID)$' { $params.Username = $val }
            '^(Password|PWD)$'               { $params.Password = $val }
            '^(SSL Mode|SslMode)$'           { $params.SslMode = $val }
            '^(Trust Server Certificate)$'    { $params.TrustServerCertificate = ($val -ieq 'true') }
        }
    }

    return $params
}

# ==============================================================================
# HÀM BỔ TRỢ: ÁNH XẠ SSL MODE KIỂU NPGSQL SANG GIÁ TRỊ LIBPQ (PGSSLMODE)
# Ví dụ: "VerifyFull" (Npgsql) -> "verify-full" (libpq). Tránh lỗi:
#   pg_dump: error: invalid sslmode value: "VerifyFull"
# ==============================================================================
function ConvertTo-LibPqSslMode {
    param([string]$SslMode)

    if ([string]::IsNullOrWhiteSpace($SslMode)) { return $null }

    $normalized = $SslMode.Trim().ToLowerInvariant().Replace('_', '-')
    switch ($normalized) {
        'require'     { return 'require' }
        'verifyfull'  { return 'verify-full' }
        'verify-full' { return 'verify-full' }
        'verifyca'    { return 'verify-ca' }
        'verify-ca'   { return 'verify-ca' }
        'prefer'      { return 'prefer' }
        'disable'     { return 'disable' }
        'allow'       { return 'allow' }
        default       { return $null }
    }
}

# ==============================================================================
# HÀM BỔ TRỢ: THỰC THI SQL QUA PSQL (MẬT KHẨU QUA BIẾN MÔI TRƯỜNG PHIÊN)
# ==============================================================================
function Invoke-PgSqlQuery {
    param(
        [string]$PsqlExe,
        [hashtable]$ConnParams,
        [string]$DatabaseName,
        [string]$Query
    )

    $args = @(
        "-h", $ConnParams.Host,
        "-p", $ConnParams.Port.ToString(),
        "-U", $ConnParams.Username,
        "-d", $DatabaseName,
        "-t", "-A",
        "-c", $Query
    )

    $output = & $PsqlExe $args 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Lenh SQL that bai (Ma thoat: $LASTEXITCODE): $output"
    }
    return $output
}

# ==============================================================================
# HÀM BỔ TRỢ: XÁC MINH BẢN BACKUP HỢP LỆ (MANIFEST + STATUS + SIZE + SHA-256)
# Dùng chung cho retention (backup-db) và lựa chọn dump (restore-drill) — H2.
# ==============================================================================
function Test-S55BackupValidity {
    param(
        [Parameter(Mandatory)]
        [string]$DumpPath
    )

    $result = @{
        IsValid     = $false
        Reason      = ""
        Sha256      = $null
        SizeBytes   = 0
        Manifest    = $null
        ManifestPath = $null
    }

    if (-not (Test-Path -LiteralPath $DumpPath)) {
        $result.Reason = "DumpNotFound"
        return $result
    }

    $dumpItem = Get-Item -LiteralPath $DumpPath
    $result.SizeBytes = $dumpItem.Length

    $manifestPath = [System.IO.Path]::ChangeExtension($DumpPath, ".json")
    $result.ManifestPath = $manifestPath

    # Điều kiện bắt buộc 1: Phải có manifest JSON đi kèm
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        $result.Reason = "ManifestNotFound"
        return $result
    }

    # Điều kiện bắt buộc 2: Manifest phải đọc và parse được
    $manifestContent = $null
    try {
        $manifestContent = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    } catch {
        $result.Reason = "ManifestUnreadable"
        return $result
    }

    # Điều kiện bắt buộc 3: status phải là VerifiedValid
    if ($manifestContent.status -ne "VerifiedValid") {
        $result.Reason = "StatusNotVerifiedValid"
        return $result
    }

    # Điều kiện bắt buộc 4: Kích thước > 1024 bytes
    if ($dumpItem.Length -le 1024) {
        $result.Reason = "SizeTooSmall"
        return $result
    }

    # Điều kiện bắt buộc 5: Tên file trong manifest phải khớp file dump
    if ($manifestContent.backupFile -ne (Split-Path $DumpPath -Leaf)) {
        $result.Reason = "BackupFileNameMismatch"
        return $result
    }

    # Điều kiện bắt buộc 6: SHA-256 trong manifest phải khớp 100% với file thực tế
    if ([string]::IsNullOrWhiteSpace($manifestContent.sha256)) {
        $result.Reason = "Sha256Missing"
        return $result
    }

    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $DumpPath).Hash
    if ($actualHash -ne $manifestContent.sha256) {
        $result.Reason = "Sha256Mismatch"
        return $result
    }

    $result.IsValid = $true
    $result.Reason = "Valid"
    $result.Sha256 = $actualHash
    $result.Manifest = $manifestContent
    return $result
}

# ==============================================================================
# HÀM BỔ TRỢ: PHÒNG VỆ MÔI TRƯỜNG DIỄN TẬP — DENY-BY-DEFAULT (H1)
# Chỉ cho phép localhost hoặc host nằm trong allowlist tường minh.
# Tên database có prefix drill KHÔNG đủ để bảo vệ cluster.
# ==============================================================================
function Assert-SafeDrillEnvironment {
    param(
        [hashtable]$TargetParams,
        [hashtable]$SourceParams,
        [string]$TargetDbName,
        [string[]]$AllowedHosts = @()
    )

    # 1. Kiểm tra tiền tố bắt buộc của Database tạm
    if (-not $TargetDbName.StartsWith("eventpulse_drill_")) {
        throw "[PHONG VE AN TOAN 1] Ten database dich phai bat dau bang tien to 'eventpulse_drill_'."
    }

    # 2. Kiểm tra xung đột với nguồn backup
    if (-not [string]::IsNullOrWhiteSpace($SourceParams.Host) -and -not [string]::IsNullOrWhiteSpace($SourceParams.Database)) {
        if ($TargetParams.Host -eq $SourceParams.Host -and $TargetDbName -eq $SourceParams.Database) {
            throw "[PHONG VE AN TOAN 2] Database dich trung hoan toan voi Database nguon! Khong dien tap de len nguon."
        }
    }

    $targetHostRaw = $TargetParams.Host.Trim()
    $targetHostLower = $targetHostRaw.ToLower()

    # 3. Danh sách domain cloud/production TUYỆT ĐỐI cấm làm đích diễn tập (bất kể allowlist)
    $forbiddenCloudDomains = @("neon.tech", "render.com", "amazonaws.com", "supabase.co", "azure.com", "google.com")
    foreach ($domain in $forbiddenCloudDomains) {
        if ($targetHostLower.Contains($domain)) {
            throw "[PHONG VE AN TOAN 3] May chu dich thuoc ha tang Cloud ($domain). Khong duoc tu y ghi vao CSDL Cloud/Production."
        }
    }

    # 4. DENY-BY-DEFAULT: Host đích bắt buộc phải là localhost hoặc nằm trong allowlist tường minh
    $allowedHostsNormalized = @("localhost", "127.0.0.1", "::1") + @($AllowedHosts | ForEach-Object { $_.Trim().ToLower() })
    if (-not ($allowedHostsNormalized -contains $targetHostLower)) {
        throw "[PHONG VE AN TOAN 4] May chu dich '$targetHostRaw' KHONG nam trong danh sach cho phep (localhost hoac allowlist). Tu choi dien tap de bao ve cluster. Neu day la may chu test cua ban, truyen -AllowedHosts '$targetHostRaw' hoac dat bien moi truong S55_DRILL_ALLOWED_HOSTS."
    }
}

# ==============================================================================
# HÀM BỔ TRỢ: CHẠY TIẾN TRÌNH VỚI TIMEOUT AN TOÀN (H3)
# Terminate tiến trình khi quá hạn và trả về trạng thái rõ ràng.
# ==============================================================================
function Invoke-ProcessWithTimeout {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter()]
        [string[]]$Arguments = @(),

        [Parameter()]
        [int]$TimeoutSeconds = 3600,

        [Parameter()]
        [string]$WorkingDirectory
    )

    # Xây dựng chuỗi tham số với quoting an toàn cho đường dẫn chứa khoảng trắng
    $quotedArgs = @($Arguments | ForEach-Object {
        if ($_ -match '\s' -and -not $_.StartsWith('"')) { "`"$_`"" } else { $_ }
    })

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $quotedArgs -join ' '
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $psi.WorkingDirectory = $WorkingDirectory
    }

    $proc = [System.Diagnostics.Process]::Start($psi)
    $finished = $proc.WaitForExit($TimeoutSeconds * 1000)

    if (-not $finished) {
        # Terminate an toàn cả cây tiến trình (nếu runtime hỗ trợ) rồi kill cứng nếu cần
        try {
            $proc.Kill($true)
        } catch {
            $proc.Kill()
        }
        $proc.WaitForExit(10000) | Out-Null
        return @{
            ExitCode  = 124
            TimedOut  = $true
            Finished  = $false
            ProcessId = $proc.Id
        }
    }

    return @{
        ExitCode  = $proc.ExitCode
        TimedOut  = $false
        Finished  = $true
        ProcessId = $proc.Id
    }
}
