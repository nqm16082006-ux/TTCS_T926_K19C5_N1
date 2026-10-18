<#
.SYNOPSIS
    S-55: RUNBOOK DIỄN TẬP KHÔI PHỤC END-TO-END (RESTORE DRILL + API SMOKE TEST)

.DESCRIPTION
    Dựng môi trường tạm CÔ LẬP hoàn toàn trong Docker (PostgreSQL + Redis + API) với
    mật khẩu ngẫu nhiên sinh trong RAM, chạy restore-drill.ps1, khởi động API trỏ vào
    database vừa khôi phục (Mock payment, SMTP tắt, AutoMigrate/AutoSeed tắt), chạy
    smoke test, sau đó dọn dẹp có xác minh nhãn sở hữu.

    AN TOÀN TUYỆT ĐỐI:
    - KHÔNG bao giờ chạm vào database nguồn (chỉ đọc file dump đã tạo từ trước).
    - KHÔNG gửi email/webhook thật (SMTP trống, payment Mock, không cấu hình PayOS).
    - KHÔNG truyền connection string chứa mật khẩu qua tham số dòng lệnh (chỉ qua env).
    - KHÔNG ghi mật khẩu xuống đĩa; biến credential được xóa sạch trong khối finally.
    - Chỉ DROP database do chính phiên drill tạo ra, sau khi xác minh ownership marker.
    - Nếu drill hoặc smoke thất bại: GIỮ NGUYÊN tài nguyên để điều tra (triage).
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$DumpFile,

    [Parameter()]
    [string]$BackupDir = "",

    [Parameter()]
    [int]$PgPort = 5544,

    [Parameter()]
    [int]$ApiPort = 5013,

    [Parameter()]
    [string]$EvidenceDir = "",

    [Parameter()]
    [int]$RunnerTimeoutSeconds = 600,

    [Parameter()]
    [string]$PgImage = "postgres:18-alpine",

    [Parameter()]
    [int]$ApiHealthTimeoutSeconds = 180,

    [Parameter()]
    [int]$SmokeTimeoutSeconds = 120,

    [Parameter()]
    [string[]]$AllowedHosts = @(),

    [Parameter()]
    [string]$AgeIdentityFile,

    [Parameter()]
    [switch]$SkipApiSmoke,

    [Parameter()]
    [switch]$KeepResources
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot trong param default khong duoc nap dung gio trong PowerShell 5.1
# (advanced script), nen tinh duong dan o day de tuong thich ca 5.1 lan 7.x.
if ([string]::IsNullOrWhiteSpace($BackupDir)) {
    $BackupDir = Join-Path $PSScriptRoot "..\artifacts\database-backups"
}
if ([string]::IsNullOrWhiteSpace($EvidenceDir)) {
    $EvidenceDir = Join-Path $PSScriptRoot "..\artifacts\drill-evidence"
}

# Nạp module dùng chung S-55 (Test-S55BackupValidity) và test helpers (Docker snapshot)
. (Join-Path $PSScriptRoot "s55-common.ps1")
$helpersPath = Join-Path $PSScriptRoot "..\tests\S55-TestHelpers.ps1"
if (-not (Test-Path -LiteralPath $helpersPath)) {
    throw "[LOI MOI TRUONG] Khong tim thay tests/S55-TestHelpers.ps1. Repo khong day du."
}
. $helpersPath

# ==============================================================================
# TÊN TÀI NGUYÊN CỐ ĐỊNH CỦA PHIÊN DRILL
# ==============================================================================
$pgContainerName  = "s55drill_pg"
$redisContainerName = "s55drill_redis"
$apiContainerName = "s55drill_api"
$runnerContainerName = "s55drill_runner"
$netName          = "s55drill_net"
$volName          = "s55drill_pgdata"

$sessionId = 's55_drill_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [guid]::NewGuid().ToString().Substring(0, 8)

$networkCreated = $false
$volumeCreated  = $false
$pgCreated      = $false
$redisCreated   = $false
$apiCreated     = $false
$runnerCreated  = $false
$drillPassed    = $false
$smokePassed    = $false
$cleanupPassed  = $false
$finalPassed    = $false
$failureReason  = $null
$randomPwd      = $null
$connStr        = $null
$targetDbName   = $null
$drillRunId     = $null
$cleanupErrors  = @()
$beforeMap      = @{}

Write-Host "=================================================================="
Write-Host " S-55: RESTORE DRILL END-TO-END (ISOLATED DOCKER ENVIRONMENT)"
Write-Host " Session ID : $sessionId"
Write-Host "=================================================================="

try {
    # --------------------------------------------------------------------------
    # GIAI ĐOẠN 0: PREFLIGHT
    # --------------------------------------------------------------------------
    $dockerCheck = docker version --format '{{.Server.Version}}' 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dockerCheck)) {
        throw "[ABORT] Docker khong kha dung. Kiem tra Docker Desktop/daemon."
    }

    $portConn = Get-NetTCPConnection -LocalPort $PgPort -ErrorAction SilentlyContinue
    if ($null -ne $portConn) { throw "[ABORT] Cong $PgPort dang bi chiem dung!" }
    $portConnApi = Get-NetTCPConnection -LocalPort $ApiPort -ErrorAction SilentlyContinue
    if ($null -ne $portConnApi) { throw "[ABORT] Cong $ApiPort dang bi chiem dung!" }

    foreach ($name in @($pgContainerName, $redisContainerName, $apiContainerName, $runnerContainerName)) {
        $exist = docker ps -a --filter "name=^/$name$" --format '{{.ID}}'
        if ($exist) { throw "[ABORT] Phat hien container cu '$name' chua duoc giai phong! Don dep thu cong truoc khi chay lai." }
    }
    $volExist = docker volume ls --filter "name=^$volName$" --format '{{.Name}}'
    if ($volExist) { throw "[ABORT] Phat hien volume cu '$volName' chua duoc giai phong!" }
    $netExist = docker network ls --filter "name=^$netName$" --format '{{.Name}}'
    if ($netExist) { throw "[ABORT] Phat hien network cu '$netName' chua duoc giai phong!" }

    if (-not (Test-Path -LiteralPath $EvidenceDir)) {
        New-Item -ItemType Directory -Path $EvidenceDir -Force | Out-Null
    }

    # Xác định dump: file chỉ định hoặc bản hợp lệ mới nhất
    if ([string]::IsNullOrWhiteSpace($DumpFile)) {
        if (-not (Test-Path -LiteralPath $BackupDir)) { throw "[ABORT] Thu muc backup khong ton tai: $BackupDir" }
        $candidates = Get-ChildItem -Path $BackupDir -File |
            Where-Object { $_.Name -match '^eventpulse_backup_\d{8}_\d{6}\.dump(\.age)?$' } |
            Sort-Object -Property LastWriteTime -Descending
        if ($candidates.Count -eq 0) { throw "[ABORT] Khong tim thay ban backup nao trong: $BackupDir" }
        $DumpFile = $candidates[0].FullName
        Write-Host "[PREFLIGHT] Tu chon ban backup moi nhat: $(Split-Path $DumpFile -Leaf)"
    }
    if (-not (Test-Path -LiteralPath $DumpFile)) { throw "[ABORT] File dump khong ton tai: $DumpFile" }
    $dumpLeaf = Split-Path $DumpFile -Leaf
    Write-Host "[PREFLIGHT] Backup dung cho dien tap: $dumpLeaf"
    Write-Host "[PREFLIGHT] Xac minh manifest/hash se do restore-drill.ps1 thuc hien truoc khi restore (H2)."

    # Snapshot container trước phiên
    $snapshotBeforeRaw = docker ps -a --format '{{.ID}} | {{.Names}} | {{.Status}} | {{.Ports}}'
    Set-Content -Path (Join-Path $EvidenceDir "docker_snapshot_before.txt") -Value $snapshotBeforeRaw
    $beforeMap = Parse-DockerSnapshot -SnapshotLines ($snapshotBeforeRaw -split "`r?`n")
    Write-Host "[PREFLIGHT] Snapshot truoc phien: $($beforeMap.Count) container da ghi nhan."

    # --------------------------------------------------------------------------
    # GIAI ĐOẠN 1: DỰNG POSTGRESQL TẠM CÔ LẬP
    # --------------------------------------------------------------------------
    $netOutput = docker network create --label "s55.session.id=$sessionId" --label "s55.created.by=S55-Drill" $netName
    if ($LASTEXITCODE -ne 0) { throw "[ABORT] Khong the tao network $netName" }
    $networkCreated = $true

    $volOutput = docker volume create --label "s55.session.id=$sessionId" --label "s55.created.by=S55-Drill" $volName
    if ($LASTEXITCODE -ne 0) { throw "[ABORT] Khong the tao volume $volName" }
    $volumeCreated = $true

    # Mật khẩu ngẫu nhiên DUY NHẤT trong RAM phiên (KHÔNG ghi xuống đĩa)
    $randomPwd = 's55_pwd_' + [guid]::NewGuid().ToString().Replace('-', '').Substring(0, 16)

    # PostgreSQL 18+ yêu cầu mount volume tại /var/lib/postgresql (không phải /var/lib/postgresql/data)
    $pgId = docker run -d `
        --name $pgContainerName `
        --label "s55.session.id=$sessionId" `
        --label "s55.created.by=S55-Drill" `
        --network $netName `
        -p "127.0.0.1:$($PgPort):5432" `
        -e "POSTGRES_DB=postgres" `
        -e "POSTGRES_USER=postgres" `
        -e "POSTGRES_PASSWORD=$randomPwd" `
        -v "${volName}:/var/lib/postgresql" `
        $PgImage
    if ($LASTEXITCODE -ne 0) { throw "[ABORT] Khong the khoi dong container PostgreSQL tam." }
    $pgCreated = $true

    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        $status = docker exec $pgContainerName pg_isready -U postgres 2>$null
        if ($status -match 'accepting connections') { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw "[ABORT] PostgreSQL tam khong san sang sau 60 giay." }
    Write-Host "[ENV UP] PostgreSQL tam san sang (container $pgContainerName, port $PgPort)."

    # --------------------------------------------------------------------------
    # GIAI ĐOẠN 2: CHẠY RESTORE DRILL TRONG RUNNER CONTAINER
    # --------------------------------------------------------------------------
    $targetDbName = 'eventpulse_drill_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [guid]::NewGuid().ToString().Substring(0, 8)
    $connStr = "Host=$pgContainerName;Port=5432;Database=postgres;Username=postgres;Password=$randomPwd"

    # Mount các thư mục vào runner; đường dẫn trong container được dùng cho -DumpFile/-ResultFile
    $hostDumpFull = (Resolve-Path -LiteralPath $DumpFile).Path
    $hostDumpDir = Split-Path $hostDumpFull -Parent
    $containerDumpPath = '/workspace/backupdir/' + $dumpLeaf
    $containerResultPath = '/workspace/evidence/drill-result.json'

    # Allowlist host cho guard H1 bên trong container + host drill PG
    $insideAllowedHosts = @($pgContainerName)
    if ($AllowedHosts.Count -gt 0) { $insideAllowedHosts += $AllowedHosts }
    $allowListEnv = $insideAllowedHosts -join ','

    # Script shell bên trong runner (KHÔNG truyền mật khẩu/connection string qua dòng lệnh)
    $drillCmdParts = @(
        "pwsh -NoProfile -File scripts/restore-drill.ps1",
        "-DumpFile '$containerDumpPath'",
        "-TargetDatabase '$targetDbName'",
        "-KeepDrillDb",
        "-ResultFile '$containerResultPath'"
    )
    if (-not [string]::IsNullOrWhiteSpace($AgeIdentityFile)) {
        $hostAgeIdentity = (Resolve-Path -LiteralPath $AgeIdentityFile).Path
        $ageIdentityDir = Split-Path $hostAgeIdentity -Parent
        $ageIdentityLeaf = Split-Path $hostAgeIdentity -Leaf
        $ageVolumeArg = "-v `"${hostAgeIdentityDir}:/workspace/ageidentity:ro`""
        $drillCmdParts += "-AgeIdentityFile '/workspace/ageidentity/$ageIdentityLeaf'"
    } else {
        $ageVolumeArg = ""
    }
    $drillCommand = $drillCmdParts -join ' '

    $containerShellScript = "set -e; apk add --no-cache powershell > /dev/null 2>&1; $drillCommand"
    Write-Host "[EXEC] Lenh drill (an toan, khong chua password): $drillCommand"

    $runnerArgs = @(
        "run", "-d",
        "--name", $runnerContainerName,
        "--label", "s55.session.id=$sessionId",
        "--label", "s55.created.by=S55-Drill-Runner",
        "--network", $netName,
        "-e", "TARGET_PG_CONNECTION=$connStr",
        "-e", "DRILL_ALLOWED_HOSTS=$allowListEnv",
        "-v", "${PWD}/scripts:/workspace/scripts:ro",
        "-v", "${hostDumpDir}:/workspace/backupdir:ro",
        "-v", "$(Resolve-Path $EvidenceDir):/workspace/evidence:rw",
        "-w", "/workspace",
        $PgImage,
        "sh", "-c", $containerShellScript
    )
    if (-not [string]::IsNullOrWhiteSpace($ageVolumeArg)) { $runnerArgs += $ageVolumeArg }

    $runnerExecResult = Invoke-RunnerWithSafeTimeout -SessionId $sessionId `
        -RunnerName $runnerContainerName `
        -TimeoutSeconds $RunnerTimeoutSeconds `
        -StartRunnerBlock {
            $rId = & docker @runnerArgs
            if ($LASTEXITCODE -ne 0) { throw "Khong the start runner container." }
            $script:runnerCreated = $true
            return $rId
        } `
        -CheckRunnerStatusBlock {
            $insp = docker inspect --format='{{.State.Running}} {{.State.ExitCode}}' $runnerContainerName 2>$null
            if ($null -eq $insp) { return @{ IsRunning = $false; ExitCode = -1 } }
            $parts = $insp.Trim() -split ' '
            return @{ IsRunning = ($parts[0] -eq 'true'); ExitCode = [int]$parts[1] }
        } `
        -GetRunnerLogsBlock { return (docker logs $runnerContainerName 2>&1) } `
        -VerifyOwnershipBlock {
            $rJson = docker inspect $runnerContainerName 2>$null | ConvertFrom-Json
            return ($null -ne $rJson -and $rJson.Config.Labels.'s55.session.id' -eq $sessionId)
        } `
        -StopRunnerBlock {
            try {
                docker stop -t 5 $runnerContainerName | Out-Null
                docker rm -f $runnerContainerName | Out-Null
                $script:runnerCreated = $false
                return @{ Success = $true }
            } catch {
                return @{ Success = $false; Error = $_.Exception.Message }
            }
        }

    # Che mật khẩu khỏi log trước khi ghi bằng chứng
    $cleanLog = ($runnerExecResult.Log -join "`r`n") -replace [regex]::Escape($randomPwd), '***'
    Set-Content -Path (Join-Path $EvidenceDir "drill_console.log") -Value $cleanLog
    Set-Content -Path (Join-Path $EvidenceDir "drill_exitcode.txt") -Value $runnerExecResult.ExitCode

    if (-not $runnerExecResult.Success) {
        throw "[DRILL FAILED] $($runnerExecResult.ErrorMessage)"
    }

    # Dọn runner container sau khi lấy log (xác minh nhãn sở hữu)
    try {
        $rJson = docker inspect $runnerContainerName 2>$null | ConvertFrom-Json
        if ($null -ne $rJson -and $rJson.Config.Labels.'s55.session.id' -eq $sessionId) {
            docker rm -f $runnerContainerName | Out-Null
            $runnerCreated = $false
        }
    } catch { }

    # Đọc kết quả drill từ ResultFile
    $resultFileHost = Join-Path $EvidenceDir "drill-result.json"
    if (-not (Test-Path -LiteralPath $resultFileHost)) {
        throw "[DRILL FAILED] Khong tim thay drill-result.json — restore-drill khong hoan tat du."
    }
    $drillResult = Get-Content -LiteralPath $resultFileHost -Raw | ConvertFrom-Json
    if ($drillResult.status -ne "Success") {
        throw "[DRILL FAILED] restore-drill bao that bai: $($drillResult.error)"
    }
    $drillRunId = $drillResult.drillRunId
    $targetDbName = $drillResult.targetDatabase
    Write-Host "[DRILL PASSED] Database '$targetDbName' da khoi phuc va doi soat thanh cong."
    Write-Host " - Migrations: $($drillResult.migrationCount) | Bang: $($drillResult.tableCount) | FK: $($drillResult.fkCount) | Index: $($drillResult.indexCount) | RTO: $($drillResult.rtoSeconds)s"
    $drillPassed = $true

    # --------------------------------------------------------------------------
    # GIAI ĐOẠN 3: KHỞI ĐỘNG API TRỎ VÀO DB ĐÃ KHÔI PHỤC (MOCK + SMTP TẮT)
    # --------------------------------------------------------------------------
    if ($SkipApiSmoke) {
        Write-Warning "[SMOKE] -SkipApiSmoke duoc bat. Bo qua khoi dong API va smoke test."
    } else {
        $redisId = docker run -d `
            --name $redisContainerName `
            --label "s55.session.id=$sessionId" `
            --label "s55.created.by=S55-Drill" `
            --network $netName `
            "redis:7-alpine"
        if ($LASTEXITCODE -ne 0) { throw "[ABORT] Khong the khoi dong Redis tam." }
        $redisCreated = $true

        Write-Host "[API] Dang build image drill API tu Dockerfile (nhanh hon neu da co cache)..."
        docker build -q -t s55drill_api_image .
        if ($LASTEXITCODE -ne 0) { throw "[ABORT] Build image drill API that bai." }

        $apiConnStr = "Host=$pgContainerName;Port=5432;Database=$targetDbName;Username=postgres;Password=$randomPwd"
        $jwtKey = 's55_drill_' + [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N').Substring(0, 16)

        # Môi trường 'Drill' (KHÔNG phải Production — guard S-43 cấm Mock trong Production;
        # KHÔNG phải Development — tránh bật nhánh dev/AutoMigrate/AutoSeed mặc định của appsettings.Development.json)
        $apiId = docker run -d `
            --name $apiContainerName `
            --label "s55.session.id=$sessionId" `
            --label "s55.created.by=S55-Drill" `
            --network $netName `
            -p "127.0.0.1:$($ApiPort):5012" `
            -e "ASPNETCORE_ENVIRONMENT=Drill" `
            -e "PORT=5012" `
            -e "Database__AutoMigrate=false" `
            -e "Database__AutoSeed=false" `
            -e "PAYMENT_PROVIDER=Mock" `
            -e "SmtpSettings__Server=" `
            -e "SmtpSettings__SenderEmail=" `
            -e "SmtpSettings__Username=" `
            -e "SmtpSettings__Password=" `
            -e "JWT_KEY=$jwtKey" `
            -e "App__PublicBaseUrl=http://127.0.0.1:$ApiPort" `
            -e "ConnectionStrings__DefaultConnection=$apiConnStr" `
            -e "ConnectionStrings__Redis=$redisContainerName`:6379" `
            "s55drill_api_image"
        if ($LASTEXITCODE -ne 0) { throw "[ABORT] Khong the khoi dong container API drill." }
        $apiCreated = $true

        # Chờ health endpoint
        $healthOk = $false
        $healthStatus = ""
        for ($i = 0; $i -lt [math]::Ceiling($ApiHealthTimeoutSeconds / 5); $i++) {
            try {
                $healthResp = Invoke-RestMethod -Uri "http://127.0.0.1:$ApiPort/api/health" -TimeoutSec 5 -ErrorAction Stop
                $healthStatus = ($healthResp | ConvertTo-Json -Compress -Depth 5)
                $healthOk = $true
                break
            } catch {
                Start-Sleep -Seconds 5
            }
        }
        if (-not $healthOk) {
            $apiLog = docker logs $apiContainerName 2>&1 | Select-Object -Last 40
            throw "[SMOKE FAILED] API khong dat health sau $ApiHealthTimeoutSeconds giay. Log cuoi: $($apiLog -join ' | ')"
        }
        Write-Host "[API] Health OK: $healthStatus"

        # SMOKE TESTS trên dữ liệu đã khôi phục
        $smokeResults = @()
        $smokePass = $true
        $sw = [System.Diagnostics.Stopwatch]::StartNew()

        $checks = @(
            @{ Name = "GET /api/health";            Uri = "http://127.0.0.1:$ApiPort/api/health" },
            @{ Name = "GET /api/public/events";     Uri = "http://127.0.0.1:$ApiPort/api/public/events?limit=5" },
            @{ Name = "GET /api/public/showtimes";  Uri = "http://127.0.0.1:$ApiPort/api/public/showtimes?limit=5" }
        )
        foreach ($check in $checks) {
            try {
                $resp = Invoke-RestMethod -Uri $check.Uri -TimeoutSec $SmokeTimeoutSeconds -ErrorAction Stop
                $smokeResults += [PSCustomObject]@{ Check = $check.Name; Passed = $true; Detail = "HTTP 200" }
                Write-Host "[SMOKE PASS] $($check.Name) -> HTTP 200"
            } catch {
                $smokePass = $false
                $errMsg = $_.Exception.Message
                $smokeResults += [PSCustomObject]@{ Check = $check.Name; Passed = $false; Detail = $errMsg }
                # -ErrorAction Continue de ghi duoc bao cao va tiep tuc cac check con lai
                Write-Error "[SMOKE FAIL] $($check.Name) -> $errMsg" -ErrorAction Continue
            }
        }
        $sw.Stop()

        $smokeReport = @(
            "S-55 DRILL SMOKE TEST REPORT",
            "----------------------------",
            "Session     : $sessionId",
            "Target DB   : $targetDbName",
            "API Port    : $ApiPort",
            "Elapsed     : $($sw.Elapsed.TotalSeconds) s",
            "Overall     : $(if ($smokePass) { 'PASS' } else { 'FAIL' })",
            ""
        ) -join "`r`n"
        foreach ($r in $smokeResults) {
            $smokeReport += "`r`n - [$($r.Check)] $(if ($r.Passed) { 'PASS' } else { 'FAIL' }) — $($r.Detail)"
        }
        Set-Content -Path (Join-Path $EvidenceDir "smoke_test_report.txt") -Value $smokeReport

        if (-not $smokePass) {
            throw "[SMOKE FAILED] Co it nhat mot smoke test khong dat. Xem chi tiet trong smoke_test_report.txt."
        }
        $smokePassed = $true
        Write-Host "[SMOKE PASSED] Ung dung hoat dong dung voi du lieu da khoi phuc."
    }

} catch {
    $failureReason = $_.Exception.Message
    # Ghi evidence TRUOC khi bao loi de khong mat du lieu dieu tra
    Set-Content -Path (Join-Path $EvidenceDir "failure_reason.txt") -Value $failureReason
    Write-Error "[DRILL RUNBOOK ERROR] $failureReason" -ErrorAction Continue

} finally {
    Write-Host "`n=================================================================="
    Write-Host " [CLEANUP GATE] DON DEP TAI NGUYEN CO XAC MINH QUYEN SO HUU"
    Write-Host "=================================================================="

    # Dọn runner nếu còn sót
    if ($runnerCreated) {
        try {
            $rJson = docker inspect $runnerContainerName 2>$null | ConvertFrom-Json
            if ($null -ne $rJson -and $rJson.Config.Labels.'s55.session.id' -eq $sessionId) {
                docker stop -t 5 $runnerContainerName | Out-Null
                docker rm -f $runnerContainerName | Out-Null
                $runnerCreated = $false
            }
        } catch { }
    }

    $shouldCleanupAll = ($drillPassed -and ($smokePassed -or $SkipApiSmoke)) -and -not $KeepResources

    if (-not $shouldCleanupAll) {
        Write-Warning "=================================================================="
        Write-Warning " [TRIAGE PRESERVATION] Drill/smoke THAT BAI hoac -KeepResources duoc bat."
        Write-Warning " GIU NGUYEN container '$pgContainerName' (port $PgPort), '$apiContainerName' va volume '$volName' de dieu tra."
        Write-Warning " Xoa thu cong sau khi dieu tra: docker rm -f $apiContainerName $pgContainerName $redisContainerName; docker volume rm $volName; docker network rm $netName"
        Write-Warning "=================================================================="
        $cleanupPassed = $false
    } else {
        $allCleaned = $true

        # 1. Dừng và xóa API container (xác minh nhãn)
        if ($apiCreated) {
            try {
                $aJson = docker inspect $apiContainerName 2>$null | ConvertFrom-Json
                if ($null -ne $aJson -and $aJson.Config.Labels.'s55.session.id' -eq $sessionId) {
                    docker stop -t 5 $apiContainerName | Out-Null
                    docker rm -f $apiContainerName | Out-Null
                    $apiCreated = $false
                    Write-Host "[CLEANUP] API container da duoc xoa."
                } else {
                    $cleanupErrors += "[API MISMATCH] Nhan so huu API khong khop. Giu lai."
                    $allCleaned = $false
                }
            } catch {
                $cleanupErrors += "[API CLEANUP ERROR] $($_.Exception.Message)"
                $allCleaned = $false
            }
        }

        # 2. Redis container
        if ($redisCreated) {
            try {
                $redJson = docker inspect $redisContainerName 2>$null | ConvertFrom-Json
                if ($null -ne $redJson -and $redJson.Config.Labels.'s55.session.id' -eq $sessionId) {
                    docker stop -t 5 $redisContainerName | Out-Null
                    docker rm -f $redisContainerName | Out-Null
                    $redisCreated = $false
                    Write-Host "[CLEANUP] Redis container da duoc xoa."
                } else {
                    $cleanupErrors += "[REDIS MISMATCH] Nhan so huu Redis khong khop. Giu lai."
                    $allCleaned = $false
                }
            } catch {
                $cleanupErrors += "[REDIS CLEANUP ERROR] $($_.Exception.Message)"
                $allCleaned = $false
            }
        }

        # 3. DROP database drill SAU KHI xác minh ownership marker (chỉ khi drill thành công)
        if ($drillPassed -and $pgCreated -and -not [string]::IsNullOrWhiteSpace($targetDbName) -and -not [string]::IsNullOrWhiteSpace($drillRunId)) {
            try {
                $ownerCheck = docker exec $pgContainerName psql -U postgres -d postgres -t -A -c "SELECT count(*) FROM pg_database WHERE datname = '$targetDbName';" 2>$null
                $markerCheck = docker exec $pgContainerName psql -U postgres -d $targetDbName -t -A -c "SELECT count(*) FROM ""__eventpulse_drill_ownership"" WHERE ""drill_run_id"" = '$drillRunId';" 2>$null
                $markerCount = [string]($markerCheck | Select-Object -Last 1).Trim()
                if ($ownerCheck.Trim() -eq '1' -and $markerCount -eq '1') {
                    docker exec $pgContainerName psql -U postgres -d postgres -c "DROP DATABASE `"$targetDbName`" WITH (FORCE);" | Out-Null
                    Write-Host "[CLEANUP] Database drill '$targetDbName' da duoc DROP (ownership marker xac minh)."
                } else {
                    $cleanupErrors += "[DB DROP SKIPPED] Ownership marker khong khop (db=$($ownerCheck.Trim()), marker=$markerCount). Giu lai database '$targetDbName'."
                    $allCleaned = $false
                }
            } catch {
                $cleanupErrors += "[DB DROP ERROR] $($_.Exception.Message)"
                $allCleaned = $false
            }
        }

        # 4. PostgreSQL container
        if ($pgCreated) {
            try {
                $pJson = docker inspect $pgContainerName 2>$null | ConvertFrom-Json
                if ($null -ne $pJson -and $pJson.Config.Labels.'s55.session.id' -eq $sessionId) {
                    docker stop -t 5 $pgContainerName | Out-Null
                    docker rm -f $pgContainerName | Out-Null
                    $pgCreated = $false
                    Write-Host "[CLEANUP] PostgreSQL container da duoc xoa."
                } else {
                    $cleanupErrors += "[PG MISMATCH] Nhan so huu PG khong khop. Giu lai."
                    $allCleaned = $false
                }
            } catch {
                $cleanupErrors += "[PG CLEANUP ERROR] $($_.Exception.Message)"
                $allCleaned = $false
            }
        }

        # 5. Volume
        if ($volumeCreated) {
            try {
                $vJson = docker volume inspect $volName 2>$null | ConvertFrom-Json
                if ($null -ne $vJson -and $vJson.Labels.'s55.session.id' -eq $sessionId) {
                    docker volume rm $volName | Out-Null
                    $volumeCreated = $false
                    Write-Host "[CLEANUP] Volume da duoc xoa."
                } else {
                    $cleanupErrors += "[VOLUME MISMATCH] Nhan so huu volume khong khop. Giu lai."
                    $allCleaned = $false
                }
            } catch {
                $cleanupErrors += "[VOLUME CLEANUP ERROR] $($_.Exception.Message)"
                $allCleaned = $false
            }
        }

        # 6. Network
        if ($networkCreated) {
            try {
                $nJson = docker network inspect $netName 2>$null | ConvertFrom-Json
                if ($null -ne $nJson -and $nJson.Labels.'s55.session.id' -eq $sessionId) {
                    docker network rm $netName | Out-Null
                    $networkCreated = $false
                    Write-Host "[CLEANUP] Network da duoc xoa."
                } else {
                    $cleanupErrors += "[NETWORK MISMATCH] Nhan so huu network khong khop. Giu lai."
                    $allCleaned = $false
                }
            } catch {
                $cleanupErrors += "[NETWORK CLEANUP ERROR] $($_.Exception.Message)"
                $allCleaned = $false
            }
        }

        if ($allCleaned -and $cleanupErrors.Count -eq 0) {
            $cleanupPassed = $true
            Write-Host "[CLEANUP PASSED] Toan bo tai nguyen drill da duoc giai phong sach se."
        } else {
            $cleanupPassed = $false
            Write-Warning "[CLEANUP FAILED] $($cleanupErrors -join '; ')"
        }
    }

    # Snapshot sau phiên & đối chiếu bảo toàn container ngoài phiên.
    # Chỉ đối chiếu khi đã ghi nhận snapshot trước phiên (preflight có thể abort trước khi chụp);
    # lỗi ở bước này không được ném ra ngoài finally — ghi nhận vào cleanupPassed thay vì crash.
    try {
        $snapshotAfterRaw = docker ps -a --format '{{.ID}} | {{.Names}} | {{.Status}} | {{.Ports}}'
        Set-Content -Path (Join-Path $EvidenceDir "docker_snapshot_after.txt") -Value $snapshotAfterRaw
        if ($beforeMap.Count -gt 0) {
            $afterMap = Parse-DockerSnapshot -SnapshotLines ($snapshotAfterRaw -split "`r?`n")
            $snapshotCompare = Compare-DockerSnapshots -BeforeMap $beforeMap -AfterMap $afterMap
            if ($snapshotCompare.IsIdentical) {
                Write-Host "[ISOLATION CONFIRMED] $($snapshotCompare.Message)"
            } else {
                Write-Warning "[ISOLATION ALERT] $($snapshotCompare.Message)"
                $cleanupPassed = $false
            }
        } else {
            Write-Warning "[ISOLATION SKIP] Khong co snapshot truoc phien (preflight abort truoc khi ghi nhan) — bo qua doi chieu bao toan container."
        }
    } catch {
        Write-Warning "[ISOLATION ALERT] Khong the doi chieu snapshot container: $($_.Exception.Message)"
        $cleanupPassed = $false
    }

    # Xóa credential khỏi RAM
    $randomPwd = $null
    $connStr = $null

    $finalPassed = ($drillPassed -and ($smokePassed -or $SkipApiSmoke) -and $cleanupPassed)

    Write-Host "`n=================================================================="
    Write-Host " TONG KET PHIEN DIEN TAP S-55"
    Write-Host " - Session        : $sessionId"
    Write-Host " - Drill Passed   : $drillPassed"
    Write-Host " - Smoke Passed   : $smokePassed"
    Write-Host " - Cleanup Passed : $cleanupPassed"
    Write-Host " - Final Status   : $(if ($finalPassed) { 'PASS' } else { 'FAIL' })"
    if ($failureReason) { Write-Host " - Failure Reason : $failureReason" }
    Write-Host "=================================================================="

    if (-not $finalPassed) {
        exit 1
    }
}
