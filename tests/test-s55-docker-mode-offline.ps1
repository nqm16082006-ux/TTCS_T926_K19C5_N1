<#
.SYNOPSIS
    S-55: BO KIEM THU OFFLINE CHO DOCKER MODE (POSTGRESQL CLIENT QUA CONTAINER) + PREFIX R2

.DESCRIPTION
    Kiem thu offline 100% su dung mock/fake:
    - KHONG goi docker that, KHONG ket noi DB that, KHONG goi R2 that.
    - Kiem tra chon mode Native/Docker, build docker args, env-file tam cho password,
      mount /s55work, remap localhost -> host.docker.internal, timeout + don container,
      khong lo credential trong log, va thu tu uu tien prefix R2 (param -> env -> default).
#>

$ErrorActionPreference = 'Stop'

Write-Host "=================================================================="
Write-Host " S-55: BO KIEM THU OFFLINE — DOCKER MODE + PREFIX R2 (ZERO DOCKER/DB/R2)"
Write-Host "=================================================================="

$scriptDir = Split-Path $PSScriptRoot -Parent
$commonScript = Join-Path $scriptDir "scripts\s55-common.ps1"
$uploadScript = Join-Path $scriptDir "scripts\backup-upload-r2.ps1"
if (-not (Test-Path -LiteralPath $commonScript)) { throw "Thieu scripts/s55-common.ps1" }
if (-not (Test-Path -LiteralPath $uploadScript)) { throw "Thieu scripts/backup-upload-r2.ps1" }

$passCount = 0
$failCount = 0

function Run-AssertTest([string]$TestName, [scriptblock]$TestBlock) {
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

. $commonScript
. $uploadScript

$script:recordedCalls = @()

# ==============================================================================
# NHOM 1: CHON MODE NATIVE / DOCKER
# ==============================================================================
Write-Host "`n--- [NHOM 1] RESOLVE MODE ---"

Run-AssertTest 'Mode.1: Tham so Docker -> Docker' {
    if ((Resolve-S55PgToolMode -ToolMode 'Docker') -ne 'Docker') { throw "Sai mode" }
}

Run-AssertTest 'Mode.2: Tham so Native -> Native' {
    if ((Resolve-S55PgToolMode -ToolMode 'Native') -ne 'Native') { throw "Sai mode" }
}

Run-AssertTest 'Mode.3: Tham so rong + env S55_PG_TOOL_MODE=Docker -> Docker' {
    $old = $env:S55_PG_TOOL_MODE
    try {
        $env:S55_PG_TOOL_MODE = 'Docker'
        if ((Resolve-S55PgToolMode -ToolMode '') -ne 'Docker') { throw "Khong doc duoc env" }
    } finally {
        $env:S55_PG_TOOL_MODE = $old
    }
}

Run-AssertTest 'Mode.4: Tham so rong + khong env -> Native (mac dinh)' {
    $old = $env:S55_PG_TOOL_MODE
    try {
        Remove-Item env:S55_PG_TOOL_MODE -ErrorAction SilentlyContinue
        if ((Resolve-S55PgToolMode -ToolMode '') -ne 'Native') { throw "Mac dinh phai la Native" }
    } finally {
        $env:S55_PG_TOOL_MODE = $old
    }
}

Run-AssertTest 'Mode.5: Gia tri khong hop le -> NEM LOI' {
    $caught = $false
    try { Resolve-S55PgToolMode -ToolMode 'WSL' | Out-Null } catch { $caught = $true }
    if (-not $caught) { throw "Gia tri WSL phai bi tu choi" }
}

Run-AssertTest 'Mode.6: Set-S55PgToolMode thiet lap script scope + kiem tra image mac dinh' {
    Set-S55PgToolMode -Mode 'Docker'
    if ($script:S55PgToolMode -ne 'Docker') { throw "Script scope khong dung" }
    if ([string]::IsNullOrWhiteSpace($script:S55PgDockerImage)) { throw "Thieu image mac dinh" }
    Set-S55PgToolMode -Mode 'Native'
}

# ==============================================================================
# NHOM 2: CHUYEN DUONG DAN HOST -> CONTAINER
# ==============================================================================
Write-Host "`n--- [NHOM 2] CONVERTTO-S55CONTAINERPATH ---"

Run-AssertTest 'Path.1: File nam duoi HostDir (co khoang trang) -> /s55work/...' {
    $r = ConvertTo-S55ContainerPath -PathValue 'C:\My Backup Dir\sub\file.dump' -HostDir 'C:\My Backup Dir'
    if ($r -ne '/s55work/sub/file.dump') { throw "Sai ket qua: $r" }
}

Run-AssertTest 'Path.2: Duong dan dung bang HostDir -> /s55work' {
    $r = ConvertTo-S55ContainerPath -PathValue 'C:\Data' -HostDir 'C:\Data'
    if ($r -ne '/s55work') { throw "Sai ket qua: $r" }
}

Run-AssertTest 'Path.3: Thu muc em cung tien to nhung khong thuoc HostDir -> giu nguyen' {
    $r = ConvertTo-S55ContainerPath -PathValue 'C:\Data2\x.dump' -HostDir 'C:\Data'
    if ($r -ne 'C:\Data2\x.dump') { throw "False positive tien to: $r" }
}

Run-AssertTest 'Path.4: HostDir rong hoac gia tri rong -> giu nguyen' {
    if ((ConvertTo-S55ContainerPath -PathValue 'abc' -HostDir '') -ne 'abc') { throw "Sai" }
    if ((ConvertTo-S55ContainerPath -PathValue '' -HostDir 'C:\Data') -ne '') { throw "Sai" }
}

# ==============================================================================
# NHOM 3: INVOKE-PGTOOLCOMMAND DOCKER MODE (MOCK) — ARGS, ENV-FILE, EXIT CODE
# ==============================================================================
Write-Host "`n--- [NHOM 3] INVOKE-PGTOOLCOMMAND (DOCKER, MOCK) ---"

$mockPassword = 'S3cret#Pass!'

Run-AssertTest 'Docker.1: Build dung docker args (run --rm --name, mount, env-file, image)' {
    $script:recordedCalls = @()
    $mock = {
        param($Command, $Arguments, $ArgsList, $ToolName, $ConnParams, $HostDir, $EnvFilePath, $ContainerName)
        $script:recordedCalls += [PSCustomObject]@{ Command = $Command; Args = @($ArgsList); EnvFile = $EnvFilePath; Name = $ContainerName }
        return @{ ExitCode = 0; Output = '' }
    }
    $conn = @{ Host = 'ep-x.neon.tech'; Port = 5432; Username = 'postgres'; Password = $mockPassword; SslMode = 'VerifyFull' }
    Invoke-PgToolCommand -ToolName 'pg_dump' -ToolArgs @('-h', 'ep-x.neon.tech', '-f', 'C:\My Backup Dir\sub\out.dump') `
        -ConnParams $conn -Mode 'Docker' -DockerImage 'postgres:18-alpine' -HostDir 'C:\My Backup Dir' -TimeoutSeconds 60 -MockCommandBlock $mock | Out-Null

    $call = $script:recordedCalls[0]
    $joined = $call.Args -join ' '
    if ($call.Command -ne 'docker') { throw "Command sai" }
    if ($joined -notmatch '^run --rm --name s55_pgdump_') { throw "Thieu run --rm --name: $joined" }
    if ($joined -notmatch 'postgres:18-alpine') { throw "Thieu image" }
    if ($joined -notmatch '-v C:\\My Backup Dir:/s55work ') { throw "Mount sai: $joined" }
    if ($joined -notmatch '/s55work/sub/out\.dump') { throw "Khong remap duong dan -f: $joined" }
    if ($joined -notmatch '--env-file') { throw "Thieu --env-file" }
    if ($joined -match [regex]::Escape($mockPassword)) { throw "PASSWORD BI LO TREN DONG LENH DOCKER!" }
}

Run-AssertTest 'Docker.2: Env-file chua PGPASSWORD/PGSSL, bi xoa sau khi chay' {
    $script:recordedCalls = @()
    $mock = {
        param($Command, $Arguments, $ArgsList, $ToolName, $ConnParams, $HostDir, $EnvFilePath, $ContainerName)
        $content = Get-Content -LiteralPath $EnvFilePath -Raw
        $script:recordedCalls += [PSCustomObject]@{ EnvFile = $EnvFilePath; EnvContent = $content }
        return @{ ExitCode = 0; Output = '' }
    }
    $conn = @{ Host = 'ep-x.neon.tech'; Port = 5432; Username = 'postgres'; Password = $mockPassword; SslMode = 'VerifyFull' }
    Invoke-PgToolCommand -ToolName 'psql' -ToolArgs @('-c', 'SELECT 1') -ConnParams $conn -Mode 'Docker' -DockerImage 'postgres:18-alpine' -TimeoutSeconds 60 -MockCommandBlock $mock | Out-Null

    $call = $script:recordedCalls[0]
    if ($call.EnvContent -notmatch "PGPASSWORD=$([regex]::Escape($mockPassword))") { throw "Env-file thieu PGPASSWORD" }
    if ($call.EnvContent -notmatch 'PGSSLMODE=verify-full') { throw "Env-file thieu PGSSLMODE" }
    if ($call.EnvContent -notmatch 'PGSSLROOTCERT=system') { throw "Env-file thieu PGSSLROOTCERT=system" }
    if (Test-Path -LiteralPath $call.EnvFile) { throw "Env-file KHONG duoc xoa sau khi chay!" }
}

Run-AssertTest 'Docker.3: Exit code khac 0 -> nem loi, password duoc che trong message' {
    $mock = {
        param($Command, $Arguments, $ArgsList, $ToolName, $ConnParams, $HostDir, $EnvFilePath, $ContainerName)
        return @{ ExitCode = 2; Output = "connection failed for password $($ConnParams.Password)" }
    }
    $conn = @{ Host = 'ep-x.neon.tech'; Port = 5432; Username = 'postgres'; Password = $mockPassword; SslMode = 'Prefer' }
    $caught = $null
    try {
        Invoke-PgToolCommand -ToolName 'psql' -ToolArgs @('-c', 'SELECT 1') -ConnParams $conn -Mode 'Docker' -DockerImage 'postgres:18-alpine' -TimeoutSeconds 60 -MockCommandBlock $mock | Out-Null
    } catch { $caught = $_.Exception.Message }
    if ($null -eq $caught) { throw "Phai nem loi khi exit code 2" }
    if ($caught -match [regex]::Escape($mockPassword)) { throw "Password bi lo trong message loi!" }
    if ($caught -notmatch '\*\*\*') { throw "Message loi khong duoc che password" }
}

Run-AssertTest 'Docker.4: Timeout -> goi docker rm -f container + nem loi timeout' {
    $script:recordedCalls = @()
    $mock = {
        param($Command, $Arguments, $ArgsList, $ToolName, $ConnParams, $HostDir, $EnvFilePath, $ContainerName)
        if ($Command -eq 'docker') {
            $script:recordedCalls += [PSCustomObject]@{ Command = 'docker'; Name = $ContainerName }
            return @{ TimedOut = $true }
        }
        if ($Command -eq 'docker-rm') {
            $script:recordedCalls += [PSCustomObject]@{ Command = 'docker-rm'; Args = @($Arguments) }
            return @{ ExitCode = 0 }
        }
        return @{ ExitCode = 0 }
    }
    $caught = $null
    try {
        Invoke-PgToolCommand -ToolName 'pg_dump' -ToolArgs @('-f', 'x.dump') -Mode 'Docker' -DockerImage 'postgres:18-alpine' -TimeoutSeconds 5 -MockCommandBlock $mock | Out-Null
    } catch { $caught = $_.Exception.Message }
    if ($null -eq $caught -or $caught -notmatch 'TIMEOUT') { throw "Phai nem loi timeout" }
    $rmCall = $script:recordedCalls | Where-Object { $_.Command -eq 'docker-rm' } | Select-Object -First 1
    if ($null -eq $rmCall) { throw "Khong thay lenh don container" }
    if (($rmCall.Args -join ' ') -notmatch '^rm -f s55_pgdump_') { throw "Lenh rm sai: $($rmCall.Args -join ' ')" }
}

Run-AssertTest 'Docker.5: Native mode trong Invoke-PgToolCommand van chay binh thuong (khong docker)' {
    $res = Invoke-PgToolCommand -ToolName 'psql' -ToolArgs @('/c', 'echo native-ok') `
        -Mode 'Native' -NativeExePath (Get-Command cmd.exe).Source -CaptureOutput -TimeoutSeconds 30
    if ($res.ExitCode -ne 0 -or $res.Output -notmatch 'native-ok') { throw "Native mode sai: $($res | ConvertTo-Json -Compress)" }
}

# ==============================================================================
# NHOM 4: INVOKE-PGSQLQUERY DOCKER MODE (MOCK) — REMAP LOCALHOST
# ==============================================================================
Write-Host "`n--- [NHOM 4] INVOKE-PGSQLQUERY (DOCKER, MOCK) ---"

Run-AssertTest 'Sql.1: Host localhost -> host.docker.internal trong container, khong lo password' {
    $script:recordedCalls = @()
    $mock = {
        param($Command, $Arguments, $ArgsList, $ToolName, $ConnParams, $HostDir, $EnvFilePath, $ContainerName)
        $script:recordedCalls += [PSCustomObject]@{ Args = @($ArgsList) }
        return @{ ExitCode = 0; Output = "42" }
    }
    Set-S55PgToolMode -Mode 'Docker' -DockerImage 'postgres:18-alpine'
    try {
        $out = Invoke-PgSqlQuery -PsqlExe $null `
            -ConnParams @{ Host = 'localhost'; Port = 5432; Username = 'postgres'; Password = $mockPassword; SslMode = 'Prefer' } `
            -DatabaseName 'postgres' -Query 'SELECT 42' -MockCommandBlock $mock
    } finally {
        Set-S55PgToolMode -Mode 'Native'
    }
    $joined = $script:recordedCalls[0].Args -join ' '
    if ($out.Trim() -ne '42') { throw "Output sai: $out" }
    if ($joined -notmatch '-h host\.docker\.internal') { throw "Khong remap localhost: $joined" }
    if ($joined -match [regex]::Escape($mockPassword)) { throw "PASSWORD BI LO!" }
}

# ==============================================================================
# NHOM 5: INVOKE-PGPROCESSWITHTIMEOUT — PASSTHROUGH NATIVE
# ==============================================================================
Write-Host "`n--- [NHOM 5] INVOKE-PGPROCESSWITHTIMEOUT ---"

Run-AssertTest 'Proc.1: Native passthrough tra dung ExitCode/TimedOut' {
    $cmdExe = (Get-Command cmd.exe).Source
    $res = Invoke-PgProcessWithTimeout -ToolName 'pg_restore' -ToolArgs @('/c', 'exit 3') -NativeExePath $cmdExe -TimeoutSeconds 30 -Mode 'Native'
    if ($res.ExitCode -ne 3 -or $res.TimedOut) { throw "Passthrough sai: $($res | ConvertTo-Json -Compress)" }
}

# ==============================================================================
# NHOM 6: PREFIX R2 — PARAM -> ENV -> DEFAULT
# ==============================================================================
Write-Host "`n--- [NHOM 6] PREFIX R2 ---"

Run-AssertTest 'Prefix.1: Tham so co gia tri -> dung tham so' {
    if ((Resolve-S55R2Prefix -R2Prefix 's55') -ne 's55') { throw "Sai" }
}

Run-AssertTest 'Prefix.2: Tham so rong + env R2_PREFIX=s55/ -> dung env' {
    $old = $env:R2_PREFIX
    try {
        $env:R2_PREFIX = 's55/'
        if ((Resolve-S55R2Prefix -R2Prefix '') -ne 's55/') { throw "Khong doc duoc env R2_PREFIX" }
    } finally {
        $env:R2_PREFIX = $old
    }
}

Run-AssertTest 'Prefix.3: Tham so rong + khong env -> mac dinh database-backups' {
    $old = $env:R2_PREFIX
    try {
        Remove-Item env:R2_PREFIX -ErrorAction SilentlyContinue
        if ((Resolve-S55R2Prefix -R2Prefix '') -ne 'database-backups') { throw "Sai mac dinh" }
    } finally {
        $env:R2_PREFIX = $old
    }
}

Run-AssertTest 'Prefix.4: Validate-R2Config chuan hoa prefix tu Config.Prefix (s55/ -> s55)' {
    $cfg = Validate-R2Config -Config @{
        AccountId       = "0123456789abcdef0123456789abcdef"
        BucketName      = "s55-postgres-backup"
        AccessKeyId     = "test_key_id"
        SecretAccessKey = "test_secret_key"
        Prefix          = "s55/"
    }
    if ($cfg.Prefix -ne 's55') { throw "Sai prefix: $($cfg.Prefix)" }
}

Run-AssertTest 'Prefix.5: Validate-R2Config dung env R2_PREFIX khi Config.Prefix rong' {
    $old = $env:R2_PREFIX
    try {
        $env:R2_PREFIX = 's55/'
        $cfg = Validate-R2Config -Config @{
            AccountId       = "0123456789abcdef0123456789abcdef"
            BucketName      = "s55-postgres-backup"
            AccessKeyId     = "test_key_id"
            SecretAccessKey = "test_secret_key"
        }
        if ($cfg.Prefix -ne 's55') { throw "Sai prefix: $($cfg.Prefix)" }
    } finally {
        $env:R2_PREFIX = $old
    }
}

Run-AssertTest 'Prefix.6: Validate-R2Config mac dinh database-backups khi ca hai deu rong' {
    $old = $env:R2_PREFIX
    try {
        Remove-Item env:R2_PREFIX -ErrorAction SilentlyContinue
        $cfg = Validate-R2Config -Config @{
            AccountId       = "0123456789abcdef0123456789abcdef"
            BucketName      = "s55-postgres-backup"
            AccessKeyId     = "test_key_id"
            SecretAccessKey = "test_secret_key"
        }
        if ($cfg.Prefix -ne 'database-backups') { throw "Sai mac dinh: $($cfg.Prefix)" }
    } finally {
        $env:R2_PREFIX = $old
    }
}

# ==============================================================================
# NHOM 7: F1 — QUET SACH ENV-FILE TAM CON SOT (s55_pgenv_*)
# ==============================================================================
Write-Host "`n--- [NHOM 7] F1: CLEANUP ENV-FILE CON SOT ---"

$testTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('s55_test_root_' + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testTempRoot -Force | Out-Null

Run-AssertTest 'F1.1: Chi xoa thu muc dung mau CU hon nguong; giu moi + ngoai mau' {
    $old = Join-Path $testTempRoot ('s55_pgenv_' + ('a' * 32))
    $new = Join-Path $testTempRoot ('s55_pgenv_' + ('b' * 32))
    $otherFile = Join-Path $testTempRoot 's55_pgenv_notes.txt'
    $foreign = Join-Path $testTempRoot 'my_own_dir'
    New-Item -ItemType Directory -Path $old, $new, $foreign -Force | Out-Null
    Set-ItemProperty -LiteralPath $old -Name LastWriteTime -Value (Get-Date).AddDays(-3)
    New-Item -ItemType File -Path $otherFile -Force | Out-Null

    $removed = Remove-S55StalePgEnvDirs -TempRoot $testTempRoot -OlderThanHours 24 -PassThru
    if (Test-Path -LiteralPath $old) { throw "Thu muc cu khong bi xoa" }
    if (-not (Test-Path -LiteralPath $new)) { throw "Thu muc moi bi xoa nham!" }
    if (-not (Test-Path -LiteralPath $foreign)) { throw "Thu muc ngoai mau bi xoa nham!" }
    if (-not (Test-Path -LiteralPath $otherFile)) { throw "File ngoai mau bi xoa nham!" }
    if ($removed.Count -ne 1) { throw "So luong xoa sai: $($removed.Count)" }
}

Run-AssertTest 'F1.2: Bo qua reparse point (junction) — target khong bi xoa' {
    $target = Join-Path $testTempRoot ('real_dir_' + [Guid]::NewGuid().ToString('N'))
    $junc = Join-Path $testTempRoot ('s55_pgenv_' + ('c' * 32))
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $junctionOk = $true
    try { New-Item -ItemType Junction -Path $junc -Target $target -Force | Out-Null } catch { $junctionOk = $false }
    if ($junctionOk) {
        Set-ItemProperty -LiteralPath $junc -Name LastWriteTime -Value (Get-Date).AddDays(-3)
        Remove-S55StalePgEnvDirs -TempRoot $testTempRoot -OlderThanHours 24 | Out-Null
        if (-not (Test-Path -LiteralPath $target)) { throw "Target cua junction bi xoa nham!" }
        Remove-Item -LiteralPath $junc -Force
        Remove-Item -LiteralPath $target -Recurse -Force
    } else {
        Write-Host "   (SKIP) Khong tao duoc junction tren may nay — bo qua phan reparse point"
    }
}

Run-AssertTest 'F1.3: Thu muc ngoai TempRoot khong bao gio bi dung cham' {
    $outside = Join-Path ([System.IO.Path]::GetTempPath()) ('s55_outside_' + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $outside -Force | Out-Null
    $staleInOutside = Join-Path $outside ('s55_pgenv_' + ('d' * 32))
    New-Item -ItemType Directory -Path $staleInOutside -Force | Out-Null
    Set-ItemProperty -LiteralPath $staleInOutside -Name LastWriteTime -Value (Get-Date).AddDays(-3)
    Remove-S55StalePgEnvDirs -TempRoot $testTempRoot -OlderThanHours 24 | Out-Null
    if (-not (Test-Path -LiteralPath $staleInOutside)) { throw "Thu muc ngoai TempRoot bi xoa!" }
    Remove-Item -LiteralPath $outside -Recurse -Force
}

Run-AssertTest 'F1.4: Cleanup that bai (file bi khoa) chi canh bao, khong lam do chuong trinh' {
    $locked = Join-Path $testTempRoot ('s55_pgenv_' + ('e' * 32))
    New-Item -ItemType Directory -Path $locked -Force | Out-Null
    $lockedFile = Join-Path $locked 'pg.env'
    [System.IO.File]::WriteAllText($lockedFile, 'x')
    Set-ItemProperty -LiteralPath $locked -Name LastWriteTime -Value (Get-Date).AddDays(-3)
    $fs = [System.IO.File]::Open($lockedFile, 'Open', 'Read', 'None')
    try {
        Remove-S55StalePgEnvDirs -TempRoot $testTempRoot -OlderThanHours 24 | Out-Null
    } finally {
        $fs.Close()
        Remove-Item -LiteralPath $locked -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ==============================================================================
# NHOM 8: F2 — CHONG ENV-FILE INJECTION (PASSWORD CHUA CR/LF)
# ==============================================================================
Write-Host "`n--- [NHOM 8] F2: CHONG ENV-FILE INJECTION ---"

Run-AssertTest 'F2.1: Password chua CR -> tu choi TRUOC khi chay container, khong lo password' {
    $script:recordedCalls = @()
    $mock = { param($Command) $script:recordedCalls += $Command; return @{ ExitCode = 0; Output = '' } }
    $caught = $null
    try {
        Invoke-PgToolCommand -ToolName 'psql' -ToolArgs @('-c', 'SELECT 1') -ConnParams @{ Host = 'h'; Port = 5432; Username = 'u'; Password = "abc`rdef"; SslMode = 'Prefer' } -Mode 'Docker' -DockerImage 'postgres:18-alpine' -TimeoutSeconds 60 -MockCommandBlock $mock | Out-Null
    } catch { $caught = $_.Exception.Message }
    if ($null -eq $caught -or $caught -notmatch 'xuong dong') { throw "Phai tu choi password CR" }
    if ($caught -match 'abc') { throw "Password bi lo trong loi!" }
    if ($script:recordedCalls.Count -ne 0) { throw "Container khong duoc chay khi password khong hop le" }
}

Run-AssertTest 'F2.2: Password chua LF -> tu choi truoc khi chay container' {
    $script:recordedCalls = @()
    $mock = { param($Command) $script:recordedCalls += $Command; return @{ ExitCode = 0; Output = '' } }
    $caught = $null
    try {
        Invoke-PgToolCommand -ToolName 'psql' -ToolArgs @('-c', 'SELECT 1') -ConnParams @{ Host = 'h'; Port = 5432; Username = 'u'; Password = "abc`ndef"; SslMode = 'Prefer' } -Mode 'Docker' -DockerImage 'postgres:18-alpine' -TimeoutSeconds 60 -MockCommandBlock $mock | Out-Null
    } catch { $caught = $_.Exception.Message }
    if ($null -eq $caught -or $caught -notmatch 'xuong dong') { throw "Phai tu choi password LF" }
    if ($script:recordedCalls.Count -ne 0) { throw "Container khong duoc chay khi password khong hop le" }
}

Run-AssertTest 'F2.3: Password chua CRLF -> tu choi truoc khi chay container' {
    $script:recordedCalls = @()
    $mock = { param($Command) $script:recordedCalls += $Command; return @{ ExitCode = 0; Output = '' } }
    $caught = $null
    try {
        Invoke-PgToolCommand -ToolName 'psql' -ToolArgs @('-c', 'SELECT 1') -ConnParams @{ Host = 'h'; Port = 5432; Username = 'u'; Password = "abc`r`ndef"; SslMode = 'Prefer' } -Mode 'Docker' -DockerImage 'postgres:18-alpine' -TimeoutSeconds 60 -MockCommandBlock $mock | Out-Null
    } catch { $caught = $_.Exception.Message }
    if ($null -eq $caught -or $caught -notmatch 'xuong dong') { throw "Phai tu choi password CRLF" }
    if ($script:recordedCalls.Count -ne 0) { throw "Container khong duoc chay khi password khong hop le" }
}

Run-AssertTest 'F2.4: Password thong thuong van duoc chap nhan' {
    $script:recordedCalls = @()
    $mock = { param($Command, $EnvFilePath) $script:recordedCalls += $Command; return @{ ExitCode = 0; Output = '' } }
    Invoke-PgToolCommand -ToolName 'psql' -ToolArgs @('-c', 'SELECT 1') -ConnParams @{ Host = 'h'; Port = 5432; Username = 'u'; Password = 'S3cret#Pass!'; SslMode = 'Prefer' } -Mode 'Docker' -DockerImage 'postgres:18-alpine' -TimeoutSeconds 60 -MockCommandBlock $mock | Out-Null
    if ($script:recordedCalls.Count -ne 1) { throw "Phai chay container voi password hop le" }
}

# ==============================================================================
# NHOM 9: F3 — STDERR TRONG LOI DOCKER (CHAN DOAN + MASK)
# ==============================================================================
Write-Host "`n--- [NHOM 9] F3: STDERR TRONG LOI DOCKER ---"

Run-AssertTest 'F3.1: Loi docker co ca stdout lan stderr (mock Error) trong message, password duoc che' {
    $mock = {
        param($Command, $Arguments, $ArgsList, $ToolName, $ConnParams, $HostDir, $EnvFilePath, $ContainerName)
        return @{ ExitCode = 125; Output = 'stdout-line'; Error = "docker: Error response from daemon: image not found $($ConnParams.Password)" }
    }
    $conn = @{ Host = 'ep-x.neon.tech'; Port = 5432; Username = 'postgres'; Password = $mockPassword; SslMode = 'Prefer' }
    $caught = $null
    try {
        Invoke-PgToolCommand -ToolName 'pg_dump' -ToolArgs @('-f', 'x.dump') -ConnParams $conn -Mode 'Docker' -DockerImage 'postgres:18-alpine' -TimeoutSeconds 60 -MockCommandBlock $mock | Out-Null
    } catch { $caught = $_.Exception.Message }
    if ($null -eq $caught) { throw "Phai nem loi" }
    if ($caught -notmatch 'stdout-line') { throw "Thieu stdout trong loi" }
    if ($caught -notmatch 'Error response from daemon') { throw "Thieu stderr trong loi" }
    if ($caught -match [regex]::Escape($mockPassword)) { throw "Password bi lo trong loi!" }
    if ($caught -notmatch '\*\*\*') { throw "Khong mask password" }
}

Run-AssertTest 'F3.2: Khong CaptureOutput van kem noi dung loi trong exception' {
    $mock = { param($Command) return @{ ExitCode = 1; Output = 'o'; Error = 'e-detail' } }
    $caught = $null
    try {
        Invoke-PgToolCommand -ToolName 'pg_dump' -ToolArgs @('-f', 'x.dump') -Mode 'Docker' -DockerImage 'postgres:18-alpine' -TimeoutSeconds 60 -MockCommandBlock $mock | Out-Null
    } catch { $caught = $_.Exception.Message }
    if ($caught -notmatch 'e-detail') { throw "Thieu chi tiet loi khi khong CaptureOutput: $caught" }
}

# ==============================================================================
# NHOM 10: F5 — RETENTION LOI SAU KHI UPLOAD DA XAC MINH (KHONG CHAN BACKUP)
# ==============================================================================
Write-Host "`n--- [NHOM 10] F5: RETENTION NON-BLOCKING SAU UPLOAD VERIFIED ---"

$backupScript = Join-Path $scriptDir "scripts\backup-db.ps1"

Run-AssertTest 'F5.1: Retention thanh cong -> Status Success' {
    $mock = {
        param($Command, $Arguments, $R2Config)
        if ($Arguments[0] -eq 'lsjson') { return @() }
        return @{ ExitCode = 0 }
    }
    $res = & $backupScript -TestOnlyOffsite -OffsiteTestInputs @{
        RetentionSafeTest = @{
            Config    = @{ AccountId = '0123456789abcdef0123456789abcdef'; BucketName = 's55-postgres-backup'; AccessKeyId = 'k'; SecretAccessKey = 's'; Prefix = 's55' }
            Limit     = 14
            RcloneExe = 'rclone'
            MockBlock = $mock
        }
    }
    if ($res.Status -ne 'Success') { throw "Sai trang thai: $($res.Status)" }
}

Run-AssertTest 'F5.2: Retention loi transient -> RetentionFailedNonBlocking, KHONG nem loi' {
    $mock = {
        param($Command, $Arguments, $R2Config)
        if ($Arguments[0] -eq 'lsjson') { throw 'mang transient loi' }
        return @{ ExitCode = 0 }
    }
    $res = & $backupScript -TestOnlyOffsite -OffsiteTestInputs @{
        RetentionSafeTest = @{
            Config    = @{ AccountId = '0123456789abcdef0123456789abcdef'; BucketName = 's55-postgres-backup'; AccessKeyId = 'k'; SecretAccessKey = 's'; Prefix = 's55' }
            Limit     = 14
            RcloneExe = 'rclone'
            MockBlock = $mock
        }
    }
    if ($res.Status -ne 'RetentionFailedNonBlocking') { throw "Sai trang thai: $($res.Status)" }
    if ($res.Error -notmatch 'mang transient loi') { throw "Thieu nguyen nhan loi: $($res.Error)" }
}

# ==============================================================================
Write-Host "`n=================================================================="
Write-Host " TONG KET KIEM THU OFFLINE CHO S-55 DOCKER MODE + PREFIX R2"
Write-Host " - So test PASS : $passCount"
Write-Host " - So test FAIL : $failCount"
Write-Host "=================================================================="
if ($failCount -gt 0) { exit 1 }
