<#
.SYNOPSIS
    S-55: Module dùng chung cho backup, restore drill và offsite upload.

.DESCRIPTION
    - Get-PgToolExecutable: Tìm công cụ PostgreSQL (pg_dump/pg_restore/psql) qua PATH hoặc thư mục chuẩn.
    - Parse-PgConnectionString: Phân tích chuỗi kết nối an toàn (hỗ trợ mật khẩu chứa ký tự đặc biệt).
    - Invoke-PgSqlQuery: Thực thi truy vấn SQL qua psql, không để lộ mật khẩu (native hoặc Docker mode).
    - Test-S55BackupValidity: Xác minh bản backup hợp lệ (manifest + status + kích thước + SHA-256). Dùng chung cho retention và restore drill.
    - Assert-SafeDrillEnvironment: Phòng vệ môi trường drill — DENY-BY-DEFAULT (H1): chỉ cho phép localhost hoặc host trong allowlist tường minh; cấm tuyệt đối cloud/production.
    - Invoke-ProcessWithTimeout: Chạy tiến trình kèm timeout an toàn (H3): terminate process khi quá hạn và trả trạng thái rõ ràng.
    - Resolve-S55PgToolMode / Set-S55PgToolMode: Chọn Native hoặc Docker cho pg_dump/pg_restore/psql (param hoặc env S55_PG_TOOL_MODE).
    - Invoke-PgToolCommand / Invoke-PgProcessWithTimeout: Trình điều khiển chạy công cụ PostgreSQL ở cả 2 mode; Docker mode dùng --env-file tạm cho mật khẩu, mount tối thiểu /s55work, remap localhost -> host.docker.internal, dọn container khi timeout.
    - ConvertTo-S55ContainerPath: Đổi đường dẫn host -> đường dẫn /s55work trong container.
    - Resolve-S55R2Prefix: Ưu tiên prefix R2: tham số -> env R2_PREFIX -> mặc định.
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
# HÀM BỔ TRỢ: THỰC THI SQL QUA PSQL (MẬT KHẨU QUA BIẾN MÔI TRƯỜNG PHIÊN / ENV-FILE)
# - Native: mật khẩu qua $env:PGPASSWORD của process (không in ra log).
# - Docker: mật khẩu qua --env-file tạm (KHÔNG đưa lên command line docker).
# Mode được lấy từ script-scope do Set-S55PgToolMode thiết lập (mặc định Native).
# ==============================================================================
function Invoke-PgSqlQuery {
    param(
        [string]$PsqlExe,
        [hashtable]$ConnParams,
        [string]$DatabaseName,
        [string]$Query,

        [Parameter()]
        [string]$Mode,

        [Parameter()]
        [string]$DockerImage,

        [Parameter()]
        [string]$HostDir,

        [Parameter()]
        [scriptblock]$MockCommandBlock
    )

    $psqlArgs = @(
        "-h", $ConnParams.Host,
        "-p", $ConnParams.Port.ToString(),
        "-U", $ConnParams.Username,
        "-d", $DatabaseName,
        "-t", "-A",
        "-c", $Query
    )

    if ([string]::IsNullOrWhiteSpace($Mode)) { $Mode = $script:S55PgToolMode }
    if ([string]::IsNullOrWhiteSpace($Mode) -or $Mode -eq 'Native') {
        $output = & $PsqlExe $psqlArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Lenh SQL that bai (Ma thoat: $LASTEXITCODE): $output"
        }
        return $output
    }

    if ([string]::IsNullOrWhiteSpace($DockerImage)) { $DockerImage = $script:S55PgDockerImage }

    $result = Invoke-PgToolCommand -ToolName "psql" `
        -ToolArgs $psqlArgs `
        -ConnParams $ConnParams `
        -Mode $Mode `
        -DockerImage $DockerImage `
        -HostDir $HostDir `
        -CaptureOutput `
        -MockCommandBlock $MockCommandBlock
    return $result.Output
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

# ==============================================================================
# ================= S-55 DOCKER MODE: POSTGRESQL CLIENT QUA CONTAINER ===========
# ==============================================================================
# Cac ham nay cho phep chay pg_dump/pg_restore/psql qua Docker (image
# postgres:18-alpine) khi may Windows khong cai PostgreSQL client.
# Nguyên tắc an toàn:
#   - Password KHONG bao gio dua len command line docker: dung --env-file tam
#     (file bi xoa ngay trong khoi finally, thu muc nam trong %TEMP%).
#   - Chi mount DUY NHAT mot thu muc lam viec (HostDir) tai /s55work; quyen
#     doc/ghi toi thieu qua tuy chon -ReadOnlyHostDir.
#   - Container chay voi --rm --name; neu timeout se docker rm -f truoc khi kill.
#   - Host dich la localhost/127.0.0.1 duoc chuyen thanh host.docker.internal
#     (Docker Desktop Windows khong ho tro --network host).
#   - SSL verify-full/verify-ca dung PGSSLROOTCERT=system (Linux container).

function Resolve-S55PgToolMode {
    param([string]$ToolMode)

    if ([string]::IsNullOrWhiteSpace($ToolMode)) { $ToolMode = $env:S55_PG_TOOL_MODE }
    if ([string]::IsNullOrWhiteSpace($ToolMode)) { return 'Native' }
    if ($ToolMode -notin @('Native', 'Docker')) {
        throw "[LOI CAU HINH] S55_PG_TOOL_MODE khong hop le: '$ToolMode'. Chi chap nhan 'Native' hoac 'Docker'."
    }
    return $ToolMode
}

function Set-S55PgToolMode {
    param(
        [Parameter(Mandatory)]
        [string]$Mode,

        [Parameter()]
        [string]$DockerImage,

        [Parameter()]
        [string]$DockerExe
    )

    if ($Mode -notin @('Native', 'Docker')) {
        throw "[LOI CAU HINH] PgToolMode khong hop le: '$Mode'. Chi chap nhan 'Native' hoac 'Docker'."
    }
    $script:S55PgToolMode = $Mode
    if ([string]::IsNullOrWhiteSpace($DockerImage)) { $DockerImage = 'postgres:18-alpine' }
    $script:S55PgDockerImage = $DockerImage
    if ([string]::IsNullOrWhiteSpace($DockerExe)) { $DockerExe = 'docker' }
    $script:S55PgDockerExe = $DockerExe

    # F1: Khi dung Docker mode, quet sach env-file tam con sot tu cac lan truoc bi
    # ket thuc cung (taskkill/crash) — chi xoa thu muc dung mau, khong theo
    # reparse point, chi xoa thu muc cu hon nguong da tai lieu (24 gio).
    if ($Mode -eq 'Docker') {
        Remove-S55StalePgEnvDirs | Out-Null
    }
}

# F1: Quet sach thu muc env-file tam 's55_pgenv_*' con sot trong TEMP.
# An toan:
#   - Chi xet thu muc con truc tiep cua TEMP khop dung mau s55_pgenv_<32 hex>.
#   - Bo qua reparse point (junction/symlink) de khong xoa du lieu ngoai TEMP.
#   - Chi xoa thu muc co LastWriteTime cu hon nguong (mac dinh 24 gio).
#   - Loi xoa tung thu muc chi canh bao, khong lam do chuong trinh.
# Gioi han: khong chong duoc viec file pg.env cua LAN CHAY HIEN TAI bi con lai
# neu chinh lan do bi kill cung — file do se duoc quet trong lan chay ke tiep.
function Remove-S55StalePgEnvDirs {
    param(
        [Parameter()]
        [string]$TempRoot,

        [Parameter()]
        [int]$OlderThanHours = 24,

        [Parameter()]
        [switch]$PassThru
    )

    if ([string]::IsNullOrWhiteSpace($TempRoot)) { $TempRoot = [System.IO.Path]::GetTempPath() }
    $tempRootFull = [System.IO.Path]::GetFullPath($TempRoot.TrimEnd('\', '/'))
    if (-not (Test-Path -LiteralPath $tempRootFull -PathType Container)) { return @() }

    $cutoff = (Get-Date).AddHours(-$OlderThanHours)
    $removed = @()
    $candidates = Get-ChildItem -LiteralPath $tempRootFull -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^s55_pgenv_[0-9a-fA-F]{32}$' }

    foreach ($dir in $candidates) {
        try {
            if ($dir.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                Write-Warning "[ENV CLEANUP] Bo qua reparse point: $($dir.FullName)"
                continue
            }
            if ($dir.LastWriteTime -lt $cutoff) {
                Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction Stop
                $removed += $dir.FullName
            }
        } catch {
            Write-Warning "[ENV CLEANUP] Khong the xoa thu muc tam '$($dir.FullName)': $($_.Exception.Message)"
        }
    }

    if ($PassThru) { return $removed }
}

# Chuyen duong dan host nam duoi HostDir thanh duong dan trong container (/s55work).
# Tra ve nguyen gia tri neu khong nam duoi HostDir (tranh false positive tien to).
function ConvertTo-S55ContainerPath {
    param(
        [string]$PathValue,
        [string]$HostDir
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) { return $PathValue }
    if ([string]::IsNullOrWhiteSpace($HostDir)) { return $PathValue }

    $normalizedHost = $HostDir.TrimEnd('\', '/')
    if (-not ($PathValue -eq $normalizedHost -or
              $PathValue.StartsWith($normalizedHost + '\', [System.StringComparison]::OrdinalIgnoreCase))) {
        return $PathValue
    }

    $rest = $PathValue.Substring($normalizedHost.Length).TrimStart('\', '/')
    if ([string]::IsNullOrWhiteSpace($rest)) { return "/s55work" }
    return "/s55work/" + ($rest -replace '\\', '/')
}

# Trinh dieu khien chay lenh PostgreSQL client (pg_dump/pg_restore/psql) o
# Native mode hoac Docker mode. Dung chung cho ca Invoke-PgSqlQuery va
# Invoke-PgProcessWithTimeout.
function Invoke-PgToolCommand {
    param(
        [Parameter(Mandatory)]
        [string]$ToolName,

        [Parameter(Mandatory)]
        [string[]]$ToolArgs,

        [Parameter()]
        [hashtable]$ConnParams,

        [Parameter()]
        [string]$Mode,

        [Parameter()]
        [string]$DockerImage,

        [Parameter()]
        [string]$NativeExePath,

        [Parameter()]
        [string]$HostDir,

        [Parameter()]
        [switch]$ReadOnlyHostDir,

        [Parameter()]
        [int]$TimeoutSeconds = 3600,

        [Parameter()]
        [switch]$CaptureOutput,

        [Parameter()]
        [scriptblock]$MockCommandBlock
    )

    if ([string]::IsNullOrWhiteSpace($Mode)) { $Mode = $script:S55PgToolMode }
    if ([string]::IsNullOrWhiteSpace($Mode)) { $Mode = 'Native' }
    if ($Mode -notin @('Native', 'Docker')) {
        throw "[LOI CAU HINH] PgToolMode khong hop le: '$Mode'. Chi chap nhan 'Native' hoac 'Docker'."
    }

    # ============================ NATIVE MODE ============================
    if ($Mode -eq 'Native') {
        if ([string]::IsNullOrWhiteSpace($NativeExePath)) {
            throw "[LOI MOI TRUONG] NativeExePath rong khi chay Native mode cho '$ToolName'."
        }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $NativeExePath
        $quotedArgs = @($ToolArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } })
        $psi.Arguments = $quotedArgs -join ' '
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        # F3: Luon redirect ca 2 stream va doc BAT DONG BO de tranh deadlock
        # (khi child ghi day buffer stderr trong luc ta doi stdout).
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true

        $proc = [System.Diagnostics.Process]::Start($psi)
        $stdOutTask = $proc.StandardOutput.ReadToEndAsync()
        $stdErrTask = $proc.StandardError.ReadToEndAsync()
        $finished = $proc.WaitForExit($TimeoutSeconds * 1000)
        if (-not $finished) {
            try { $proc.Kill($true) } catch { $proc.Kill() }
            $proc.WaitForExit(10000) | Out-Null
            throw "[TIMEOUT] Lenh $ToolName vuot qua $TimeoutSeconds giay va da bi dung an toan (H3)."
        }
        $stdOut = $stdOutTask.Result
        $stdErr = $stdErrTask.Result
        if ($proc.ExitCode -ne 0) {
            $errDetail = ($stdOut + $stdErr)
            throw "Lenh $ToolName that bai voi ma thoat $($proc.ExitCode): $errDetail"
        }
        return @{ ExitCode = $proc.ExitCode; Output = if ($CaptureOutput) { ($stdOut + $stdErr) } else { $null }; TimedOut = $false }
    }

    # ============================ DOCKER MODE ============================
    if ([string]::IsNullOrWhiteSpace($DockerImage)) { $DockerImage = $script:S55PgDockerImage }
    if ([string]::IsNullOrWhiteSpace($DockerImage)) { $DockerImage = 'postgres:18-alpine' }
    $dockerExe = $script:S55PgDockerExe
    if ([string]::IsNullOrWhiteSpace($dockerExe)) { $dockerExe = 'docker' }

    # 1. Remap duong dan host -> container (mount duy nhat HostDir tai /s55work)
    $containerToolArgs = @()
    $fullHostDir = $null
    if (-not [string]::IsNullOrWhiteSpace($HostDir)) {
        $fullHostDir = [System.IO.Path]::GetFullPath($HostDir)
        foreach ($a in $ToolArgs) {
            $containerToolArgs += (ConvertTo-S55ContainerPath -PathValue $a -HostDir $fullHostDir)
        }
    } else {
        $containerToolArgs = $ToolArgs
    }

    # 2. Remap host dich local -> host.docker.internal (Docker Desktop khong co host network)
    if ($null -ne $ConnParams) {
        $targetHost = [string]$ConnParams.Host
        if ($targetHost -in @('localhost', '127.0.0.1', '::1')) {
            for ($i = 0; $i -lt $containerToolArgs.Count - 1; $i++) {
                if ($containerToolArgs[$i] -eq '-h') { $containerToolArgs[$i + 1] = 'host.docker.internal' }
            }
        }
    }

    # 3. Ghi env-file tam chua PGPASSWORD/PGSSL (KHONG dua password len command line)
    $envDir = $null
    $envFilePath = $null
    if ($null -ne $ConnParams) {
        # F2: Tu choi password chua CR/LF truoc khi ghi env-file — nguoc lai password
        # co the chen them dong (vd: PGSSLMODE=disable) vao bien moi truong container.
        # Khong bao gio in noi dung password trong loi.
        if (-not [string]::IsNullOrWhiteSpace($ConnParams.Password) -and $ConnParams.Password -match "[\r\n]") {
            throw "[LOI AN TOAN] Password chua ky tu xuong dong (CR/LF) — tu choi tao env-file de tranh injection vao bien moi truong container."
        }

        $envLines = @()
        if (-not [string]::IsNullOrWhiteSpace($ConnParams.Password)) {
            $envLines += "PGPASSWORD=$($ConnParams.Password)"
        }
        $libpqSsl = ConvertTo-LibPqSslMode -SslMode $ConnParams.SslMode
        if ($libpqSsl -in @('verify-full', 'verify-ca', 'require')) {
            $envLines += "PGSSLMODE=$libpqSsl"
            if ($libpqSsl -in @('verify-full', 'verify-ca')) { $envLines += "PGSSLROOTCERT=system" }
        }
        if ($envLines.Count -gt 0) {
            $envDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_pgenv_" + [Guid]::NewGuid().ToString("N"))
            New-Item -ItemType Directory -Path $envDir -Force | Out-Null
            # F1: Giam toi da quyen doc thu muc chua env-file ngay khi tao (best-effort,
            # chi Windows): chi user hien tai + SYSTEM, bo ke thua ACL.
            try {
                if ($env:OS -eq 'Windows_NT') {
                    $acl = New-Object System.Security.AccessControl.DirectorySecurity
                    $acl.SetAccessRuleProtection($true, $false)
                    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
                    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($currentSid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
                    $systemSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
                    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($systemSid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
                    $dirInfo = New-Object System.IO.DirectoryInfo($envDir)
                    # PS 5.1 (.NET Framework) co instance method; pwsh 7 (.NET Core) dung
                    # extension method FileSystemAclExtensions::SetAccessControl.
                    if ($dirInfo.GetType().GetMethod('SetAccessControl')) {
                        $dirInfo.SetAccessControl($acl)
                    } else {
                        [System.IO.FileSystemAclExtensions]::SetAccessControl($dirInfo, $acl)
                    }
                }
            } catch {
                Write-Warning "[ENV FILE] Khong the gioi han ACL thu muc tam: $($_.Exception.Message)"
            }
            $envFilePath = Join-Path $envDir "pg.env"
            [System.IO.File]::WriteAllLines($envFilePath, $envLines, (New-Object System.Text.UTF8Encoding($false)))
        }
    }

    # 4. Build docker args: run --rm --name ... [-v HostDir:/s55work[:ro]] [--env-file ...] image args
    $shortId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $containerName = "s55_" + $ToolName.Replace('_', '') + "_" + $shortId
    $dockerArgs = @('run', '--rm', '--name', $containerName)
    if (-not [string]::IsNullOrWhiteSpace($fullHostDir)) {
        $mountSpec = "${fullHostDir}:/s55work"
        if ($ReadOnlyHostDir) { $mountSpec = "${fullHostDir}:/s55work:ro" }
        $dockerArgs += @('-v', $mountSpec)
    }
    if (-not [string]::IsNullOrWhiteSpace($envFilePath)) { $dockerArgs += @('--env-file', $envFilePath) }
    $dockerArgs += $DockerImage
    $dockerArgs += $containerToolArgs

    try {
        # ---------- Mock seam (test offline, khong goi docker that) ----------
        if ($null -ne $MockCommandBlock) {
            $mockResult = & $MockCommandBlock -Command 'docker' -Arguments $dockerArgs -ArgsList $dockerArgs `
                -ToolName $ToolName -ConnParams $ConnParams -HostDir $fullHostDir -EnvFilePath $envFilePath -ContainerName $containerName
            if ($null -ne $mockResult -and $mockResult.ContainsKey('TimedOut') -and $mockResult.TimedOut) {
                & $MockCommandBlock -Command 'docker-rm' -Arguments @('rm', '-f', $containerName) -ContainerName $containerName
                throw "[TIMEOUT] Lenh $ToolName (Docker) vuot qua $TimeoutSeconds giay; container '$containerName' da duoc don sach (H3)."
            }
            $mockExit = 0
            if ($null -ne $mockResult -and $mockResult.ContainsKey('ExitCode')) { $mockExit = $mockResult.ExitCode }
            $mockOutput = ""
            if ($null -ne $mockResult -and $mockResult.ContainsKey('Output')) { $mockOutput = $mockResult.Output }
            # F3: mock co the mo phong stderr qua truong 'Error' — duoc gop vao loi
            # de kiem thu hanh vi chan doan cua nhanh that.
            $mockError = ""
            if ($null -ne $mockResult -and $mockResult.ContainsKey('Error')) { $mockError = $mockResult.Error }
            if ($mockExit -ne 0) {
                $safeMock = ($mockOutput + $mockError)
                if ($null -ne $ConnParams -and -not [string]::IsNullOrWhiteSpace($ConnParams.Password)) {
                    $safeMock = $safeMock -replace [regex]::Escape($ConnParams.Password), '***'
                }
                throw "Lenh $ToolName that bai voi ma thoat ${mockExit}: $safeMock"
            }
            return @{ ExitCode = $mockExit; Output = $mockOutput; TimedOut = $false }
        }

        # ---------- Docker that ----------
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $dockerExe
        $quotedDockerArgs = @($dockerArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } })
        $psi.Arguments = $quotedDockerArgs -join ' '
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        # F3: Luon redirect ca 2 stream va doc BAT DONG BO de tranh deadlock,
        # dong thoi giu stderr de chan doan loi khi exit code khac 0.
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true

        $proc = [System.Diagnostics.Process]::Start($psi)
        $stdOutTask = $proc.StandardOutput.ReadToEndAsync()
        $stdErrTask = $proc.StandardError.ReadToEndAsync()
        $finished = $proc.WaitForExit($TimeoutSeconds * 1000)

        if (-not $finished) {
            # Dung container bi treo truoc (docker run --rm chi tu xoa khi container thoat)
            $cleanupPsi = New-Object System.Diagnostics.ProcessStartInfo
            $cleanupPsi.FileName = $dockerExe
            $cleanupPsi.Arguments = "rm -f `"$containerName`""
            $cleanupPsi.UseShellExecute = $false
            $cleanupPsi.CreateNoWindow = $true
            try {
                $cleanupProc = [System.Diagnostics.Process]::Start($cleanupPsi)
                $cleanupProc.WaitForExit(30000) | Out-Null
            } catch {
                Write-Warning "[DOCKER CLEANUP] Khong the don container '$containerName': $_"
            }
            try { $proc.Kill($true) } catch { $proc.Kill() }
            $proc.WaitForExit(10000) | Out-Null
            throw "[TIMEOUT] Lenh $ToolName (Docker) vuot qua $TimeoutSeconds giay; container '$containerName' da duoc don sach (H3)."
        }

        $stdOut = $stdOutTask.Result
        $stdErr = $stdErrTask.Result

        if ($proc.ExitCode -ne 0) {
            $errDetail = ($stdOut + $stdErr)
            if ($null -ne $ConnParams -and -not [string]::IsNullOrWhiteSpace($ConnParams.Password)) {
                $errDetail = $errDetail -replace [regex]::Escape($ConnParams.Password), '***'
            }
            throw "Lenh $ToolName that bai voi ma thoat $($proc.ExitCode): $errDetail"
        }

        return @{ ExitCode = $proc.ExitCode; Output = if ($CaptureOutput) { ($stdOut + $stdErr) } else { $null }; TimedOut = $false }
    } finally {
        # Xoa env-file tam ngay khi ket thuc (thanh cong hay that bai)
        if (-not [string]::IsNullOrWhiteSpace($envDir) -and (Test-Path -LiteralPath $envDir)) {
            Remove-Item -LiteralPath $envDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# Wrapper mode-aware cho pg_restore/pg_dump khong can bat stdout (dung file
# hoac chi can exit code). Tra ve cung kieu {ExitCode; TimedOut; Finished}
# nhu Invoke-ProcessWithTimeout de cac call site hien co khong doi.
function Invoke-PgProcessWithTimeout {
    param(
        [Parameter(Mandatory)]
        [string]$ToolName,

        [Parameter(Mandatory)]
        [string[]]$ToolArgs,

        [Parameter()]
        [int]$TimeoutSeconds = 3600,

        [Parameter()]
        [string]$NativeExePath,

        [Parameter()]
        [hashtable]$ConnParams,

        [Parameter()]
        [string]$HostDir,

        [Parameter()]
        [switch]$ReadOnlyHostDir,

        [Parameter()]
        [string]$Mode,

        [Parameter()]
        [string]$DockerImage,

        [Parameter()]
        [scriptblock]$MockCommandBlock
    )

    if ([string]::IsNullOrWhiteSpace($Mode)) { $Mode = $script:S55PgToolMode }
    if ([string]::IsNullOrWhiteSpace($Mode) -or $Mode -eq 'Native') {
        if ([string]::IsNullOrWhiteSpace($NativeExePath)) {
            throw "[LOI MOI TRUONG] NativeExePath rong khi chay Native mode cho '$ToolName'."
        }
        $res = Invoke-ProcessWithTimeout -FilePath $NativeExePath -Arguments $ToolArgs -TimeoutSeconds $TimeoutSeconds
        return @{ ExitCode = $res.ExitCode; TimedOut = $res.TimedOut; Finished = $res.Finished }
    }

    $res = Invoke-PgToolCommand -ToolName $ToolName `
        -ToolArgs $ToolArgs `
        -ConnParams $ConnParams `
        -Mode $Mode `
        -DockerImage $DockerImage `
        -HostDir $HostDir `
        -ReadOnlyHostDir:$ReadOnlyHostDir `
        -TimeoutSeconds $TimeoutSeconds `
        -MockCommandBlock $MockCommandBlock
    return @{ ExitCode = $res.ExitCode; TimedOut = $false; Finished = $true }
}

# Giai quyet thu tu uu tien cua prefix R2: tham so -R2Prefix -> bien moi truong
# R2_PREFIX -> mac dinh 'database-backups'.
function Resolve-S55R2Prefix {
    param([string]$R2Prefix)

    if (-not [string]::IsNullOrWhiteSpace($R2Prefix)) { return $R2Prefix }
    if (-not [string]::IsNullOrWhiteSpace($env:R2_PREFIX)) { return $env:R2_PREFIX }
    return 'database-backups'
}
