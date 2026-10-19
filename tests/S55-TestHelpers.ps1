<#
.SYNOPSIS
    S-55 Test Helpers - Cac ham bo tro kiem thu offline va dieu phoi runner an toan.
    Duoc quan ly truc tiep trong Git (thay vi phu thuoc vao artifacts bi gitignore).
#>

function Parse-DockerSnapshot {
    param([string[]]$SnapshotLines)

    $map = @{}
    if ($null -eq $SnapshotLines) { return $map }

    foreach ($line in $SnapshotLines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split '\|' | ForEach-Object { $_.Trim() }
        if ($parts.Count -ge 2) {
            $id = $parts[0]
            $name = $parts[1]
            $map[$id] = $name
        }
    }
    return $map
}

function Compare-DockerSnapshots {
    param(
        [hashtable]$BeforeMap,
        [hashtable]$AfterMap
    )

    if ($null -eq $BeforeMap -or $BeforeMap.Count -eq 0) {
        throw "[SNAPSHOT ERROR] Snapshot ban dau rong hoac khong hop le."
    }
    if ($null -eq $AfterMap -or $AfterMap.Count -eq 0) {
        throw "[SNAPSHOT ERROR] Snapshot sau phien rong hoac khong hop le."
    }

    # 1. Kiem tra tung container ban dau: bat buoc phai con ton tai trong snapshot sau
    $missingContainers = @()
    foreach ($preId in $BeforeMap.Keys) {
        if (-not $AfterMap.ContainsKey($preId)) {
            $missingContainers += "$preId ($($BeforeMap[$preId]))"
        }
    }

    if ($missingContainers.Count -gt 0) {
        return @{
            IsIdentical = $false
            Status      = "ForeignContainersMissing"
            Missing     = $missingContainers
            Message     = "Phat hien container cu bi bien mat: $($missingContainers -join ', ')"
        }
    }

    # 2. Kiem tra cac container moi phat sinh chua duoc don dep
    $leftoverContainers = @()
    foreach ($postId in $AfterMap.Keys) {
        if (-not $BeforeMap.ContainsKey($postId)) {
            $postName = $AfterMap[$postId]
            $leftoverContainers += "$postId ($postName)"
        }
    }

    if ($leftoverContainers.Count -gt 0) {
        return @{
            IsIdentical = $false
            Status      = "LeftoverContainersDetected"
            Leftovers   = $leftoverContainers
            Message     = "Phat hien container phat sinh chua duoc don dep sach: $($leftoverContainers -join ', ')"
        }
    }

    return @{
        IsIdentical = $true
        Status      = "Preserved"
        Count       = $BeforeMap.Count
        Message     = "Toan bo $($BeforeMap.Count) container ban dau duoc bao toan 100% khong bi thay doi."
    }
}

# ==============================================================================
# TC-02: ĐỐI CHIẾU SNAPSHOT AN TOÀN KHI PREFLIGHT CÓ THỂ ABORT TRƯỚC KHI CHỤP
# Neu chua co snapshot truoc phien (preflight abort som): KHONG so sanh va
# KHONG quy cac container co san truoc do la tai nguyen roi cua phien.
# ==============================================================================
function Compare-S55SnapshotsSafe {
    param(
        [hashtable]$BeforeMap,
        [hashtable]$AfterMap
    )

    if ($null -eq $BeforeMap -or $BeforeMap.Count -eq 0) {
        return @{
            IsIdentical = $false
            Status      = "SkippedNoBeforeSnapshot"
            Message     = "Khong co snapshot truoc phien (preflight abort truoc khi ghi nhan) — bo qua doi chieu; cac container co truoc phien KHONG bi xem la tai nguyen roi."
        }
    }
    return Compare-DockerSnapshots -BeforeMap $BeforeMap -AfterMap $AfterMap
}

# ==============================================================================
# TC-02: CHỌN CỔNG HOST CÒN TRỐNG (KIỂM TRA AN TOÀN, KHÔNG HARCODE)
# Thu lan luot tu StartPort; moi ung vien deu duoc kiem tra qua ham
# -IsPortInUse (mac dinh Get-NetTCPConnection; test co the inject mock).
# ==============================================================================
function Get-S55FreeHostPort {
    param(
        [Parameter()]
        [int]$StartPort = 5436,

        [Parameter()]
        [int]$MaxAttempts = 10,

        [Parameter()]
        [scriptblock]$IsPortInUse
    )

    if ($null -eq $IsPortInUse) {
        $IsPortInUse = { param([int]$Port) $null -ne (Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue) }
    }

    for ($attempt = 0; $attempt -lt $MaxAttempts; $attempt++) {
        $candidate = $StartPort + $attempt
        if (-not (& $IsPortInUse -Port $candidate)) {
            return $candidate
        }
    }
    throw "[ABORT] Khong tim thay cong host nao trong khoang $StartPort..$($StartPort + $MaxAttempts - 1)."
}

# ==============================================================================
# TC-02: QUYẾT ĐỊNH CHẾ ĐỘ CLEANUP CUỐI PHIÊN
#   Skip     — chua tao bat ky tai nguyen nao (preflight abort som):
#              khong don gi, KHONG tuyen bo cleanup thanh cong.
#   Preserve — da tao tai nguyen nhung drill that bai: giu nguyen de triage.
#   Cleanup  — da tao tai nguyen va drill thanh cong: don co xac minh nhan.
# ==============================================================================
function Resolve-S55CleanupMode {
    param(
        [Parameter(Mandatory)]
        [bool]$DrillPassed,

        [Parameter(Mandatory)]
        [bool]$AnyResourceCreated,

        [Parameter()]
        [bool]$ContainerCreated = $false
    )

    if (-not $AnyResourceCreated) { return 'Skip' }
    if (-not $DrillPassed -and $ContainerCreated) { return 'Preserve' }
    return 'Cleanup'
}

# ==============================================================================
# TC-02 F2: GHI EVIDENCE FAILURE REASON AN TOAN — loi ghi file KHONG duoc
# che mat loi goc. Tra $true khi ghi duoc, $false kem canh bao khi khong.
# ==============================================================================
function Set-S55FailureEvidence {
    param(
        [Parameter(Mandatory)]
        [string]$FailureMessage,

        [Parameter(Mandatory)]
        [string]$EvidencePath
    )

    try {
        Set-Content -Path $EvidencePath -Value $FailureMessage
        return $true
    } catch {
        Write-Warning "[EVIDENCE ERROR] Khong the ghi file bang chung '$EvidencePath': $($_.Exception.Message)"
        return $false
    }
}

# ==============================================================================
# TC-02 F4: KIEM TRA IMAGE DOCKER TON TAI LOCAL (khong pull, khong xoa).
# DockerCommandBlock co the duoc inject mock cho test offline.
# ==============================================================================
function Test-S55DockerImagePresent {
    param(
        [Parameter(Mandatory)]
        [string]$ImageName,

        [Parameter()]
        [scriptblock]$DockerCommandBlock
    )

    if ($null -eq $DockerCommandBlock) {
        $DockerCommandBlock = {
            param([string[]]$Arguments)
            & docker @Arguments 2>$null
            return ($LASTEXITCODE -eq 0)
        }
    }

    $result = & $DockerCommandBlock -Arguments @('image', 'inspect', $ImageName)
    if ($result -is [bool]) { return $result }
    return [bool]$result
}

# ==============================================================================
# P1: THU DOCKER LOGS AN TOAN — dung ProcessStartInfo doc 2 stream bat dong bo
# (tranh deadlock), KHONG qua native PowerShell pipeline. Tren PS 5.1 voi
# $ErrorActionPreference='Stop', stderr cua native command qua 2>&1 co the
# nem NativeCommandError va lam mat toan bo log — helper nay tranh dieu do.
# - Khong nem khi stderr co noi dung.
# - Tra ve StdOut/StdErr/ExitCode/Success rieng biet.
# - Loi thu log KHONG duoc thay the exit code cua runner da thu truoc do.
# ==============================================================================
function Get-DockerLogsSafe {
    param(
        [Parameter(Mandatory)]
        [string]$ContainerName,

        [Parameter()]
        [string]$DockerExe = 'docker',

        [Parameter()]
        [string[]]$Arguments,

        [Parameter()]
        [int]$TimeoutSeconds = 60
    )

    if ($null -eq $Arguments -or $Arguments.Count -eq 0) {
        $Arguments = @('logs', $ContainerName)
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $DockerExe
    $quoted = @($Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } })
    $psi.Arguments = $quoted -join ' '
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $proc = $null
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
    } catch {
        return @{
            Success  = $false
            ExitCode = $null
            StdOut   = ""
            StdErr   = ""
            Error    = "Khong the khoi chay '$DockerExe': $($_.Exception.Message)"
        }
    }

    $stdOutTask = $proc.StandardOutput.ReadToEndAsync()
    $stdErrTask = $proc.StandardError.ReadToEndAsync()
    $finished = $proc.WaitForExit($TimeoutSeconds * 1000)
    if (-not $finished) {
        try { $proc.Kill($true) } catch { $proc.Kill() }
        $proc.WaitForExit(10000) | Out-Null
        return @{
            Success  = $false
            ExitCode = $null
            StdOut   = ""
            StdErr   = ""
            Error    = "'$DockerExe' vuot qua $TimeoutSeconds giay khi thu log."
        }
    }

    $stdOut = $stdOutTask.Result
    $stdErr = $stdErrTask.Result

    return @{
        Success  = ($proc.ExitCode -eq 0)
        ExitCode = $proc.ExitCode
        StdOut   = $stdOut
        StdErr   = $stdErr
        Error    = if ($proc.ExitCode -ne 0) { $stdErr } else { $null }
    }
}

# ==============================================================================
# HARDENING A: GUARD REPOSITORY ROOT — xac minh CWD dung truoc moi Docker op.
# Kiem tra nhieu file nhan dien (khong chi 1 file de tranh trung ten o thu muc khac).
# ==============================================================================
function Test-S55RepoRoot {
    param(
        [Parameter()]
        [string]$RootPath,

        [Parameter()]
        [string[]]$RequiredPaths = @('scripts\restore-drill.ps1', 'tests\S55-TestHelpers.ps1')
    )

    if ([string]::IsNullOrWhiteSpace($RootPath)) { $RootPath = (Get-Location).Path }
    foreach ($rel in $RequiredPaths) {
        if (-not (Test-Path -LiteralPath (Join-Path $RootPath $rel))) { return $false }
    }
    return $true
}

# ==============================================================================
# TC-02D FIX: TAO SCRIPT SHELL RUNNER DANG FILE (thay vi truyen qua argv).
# - KHONG chua dau nhay kep nao -> tranh CRT/PS 5.1 xe tham so native argv
#   (nguyen nhan exit 2 "unexpected end of file (expecting ))" cua TC-02D).
# - Toan bo stdout/stderr qua tee ghi ra log du phong (bind mount) VAN GIU
#   tren docker logs.
# - apk fail -> exit ngay voi APK_EXIT, khong chay restore.
# - DRILL_EXIT duoc capture TRUOC echo, exit code cuoi cung duoc bao toan.
# Tra ve chuoi LF-only (khong CR), dat placeholder thay the bang tham so.
# ==============================================================================
function Get-S55RunnerShellScript {
    param(
        [Parameter(Mandatory)]
        [string]$DumpPath,

        [Parameter(Mandatory)]
        [string]$AllowedHost,

        [Parameter(Mandatory)]
        [string]$LogPath
    )

    $template = @'
RUNNER_EXIT_FILE="/tmp/runner_step_exit_$$.txt"
rm -f "$RUNNER_EXIT_FILE"
{
echo RUNNER_MARK_START
apk add --no-cache postgresql16-client > /dev/null
APK_EXIT=$?
echo RUNNER_MARK_APK_EXIT=$APK_EXIT
if [ $APK_EXIT -ne 0 ]; then
    echo RUNNER_MARK_APK_FAILED
    echo $APK_EXIT > "$RUNNER_EXIT_FILE"
    exit $APK_EXIT
fi
RESTORE_VER=$(pg_restore --version 2>&1)
RESTORE_VER_EXIT=$?
echo "$RESTORE_VER"
echo RUNNER_MARK_RESTORE_VER_EXIT=$RESTORE_VER_EXIT
if [ $RESTORE_VER_EXIT -ne 0 ]; then
    echo RUNNER_MARK_RESTORE_VER_FAILED
    echo $RESTORE_VER_EXIT > "$RUNNER_EXIT_FILE"
    exit $RESTORE_VER_EXIT
fi
case "$RESTORE_VER" in
    *16.*) echo RUNNER_MARK_PG16_VERIFIED ;;
    *)
        echo RUNNER_MARK_PG16_MISMATCH
        echo 1 > "$RUNNER_EXIT_FILE"
        exit 1
        ;;
esac
DUMP_VER=$(pg_dump --version 2>&1)
DUMP_VER_EXIT=$?
echo "$DUMP_VER"
echo RUNNER_MARK_DUMP_VER_EXIT=$DUMP_VER_EXIT
if [ $DUMP_VER_EXIT -ne 0 ]; then
    echo RUNNER_MARK_DUMP_VER_FAILED
    echo $DUMP_VER_EXIT > "$RUNNER_EXIT_FILE"
    exit $DUMP_VER_EXIT
fi
echo RUNNER_MARK_TOOLS_VERIFIED
pwsh -NoProfile -File scripts/restore-drill.ps1 -DumpFile '__DUMP__' -AllowedHosts '__HOST__'
DRILL_EXIT=$?
echo RUNNER_MARK_DRILL_EXIT=$DRILL_EXIT
echo $DRILL_EXIT > "$RUNNER_EXIT_FILE"
exit $DRILL_EXIT
} 2>&1 | tee __LOG__
FINAL_EXIT=$(cat "$RUNNER_EXIT_FILE" 2>/dev/null)
rm -f "$RUNNER_EXIT_FILE"
if [ -z "$FINAL_EXIT" ]; then FINAL_EXIT=1; fi
exit $FINAL_EXIT
'@

    $script = $template.Replace('__DUMP__', $DumpPath).Replace('__HOST__', $AllowedHost).Replace('__LOG__', $LogPath)
    # Chuan hoa LF-only de script chay dung trong container Linux
    return $script.Replace("`r`n", "`n")
}

# ==============================================================================
# TC-02D FIX: LUU RUNNER SCRIPT RA HOST (UTF-8 khong BOM, LF)
# Tuong thich ca PowerShell 5.1 va PowerShell 7.
# ==============================================================================
function Save-S55RunnerScript {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string]$Content
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($FilePath)
    $parentDir = [System.IO.Path]::GetDirectoryName($resolvedPath)
    if (-not [string]::IsNullOrEmpty($parentDir) -and -not (Test-Path -LiteralPath $parentDir)) {
        New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
    }
    $lfContent = $Content.Replace("`r`n", "`n")
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($resolvedPath, $lfContent, $utf8NoBom)
    return $resolvedPath
}

# ==============================================================================
# HARDENING B: XAC MINH LOG DU PHONG TREN HOST (khong ghi, khong xoa).
# Thieu file -> canh bao ro rang + tra $false; khong bao gio nem (khong duoc
# ghi de exit code/status thuc cua runner).
# ==============================================================================
function Test-S55RunnerInternalLogPresent {
    param(
        [Parameter()]
        [string]$ExpectedPath
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedPath)) {
        Write-Warning "[P2 MISSING] Duong dan log du phong RONG — log du phong CHUA duoc xac minh."
        return $false
    }
    if (Test-Path -LiteralPath $ExpectedPath) {
        Write-Host "[P2] Log du phong da duoc xac minh: $ExpectedPath"
        return $true
    }
    Write-Warning "[P2 MISSING] Khong tim thay log du phong '$ExpectedPath' tren host — log du phong CHUA duoc xac minh (runner co the khong ghi duoc vao bind mount)."
    return $false
}

function Invoke-RunnerWithSafeTimeout {
    param(
        [string]$SessionId,
        [string]$RunnerName,
        [int]$TimeoutSeconds,
        [scriptblock]$StartRunnerBlock,
        [scriptblock]$CheckRunnerStatusBlock,
        [scriptblock]$GetRunnerLogsBlock,
        [scriptblock]$VerifyOwnershipBlock,
        [scriptblock]$StopRunnerBlock,
        [int]$PollIntervalMs = 1000
    )

    Write-Host "[RUNNER TIMEOUT] Khoi dong runner '$RunnerName' voi thoi han timeout: $TimeoutSeconds giay..."
    $runnerStartResult = & $StartRunnerBlock
    $elapsed = 0
    $finished = $false
    $runnerExitCode = $null
    $rawLog = $null
    $logFetchFailed = $false

    while ($elapsed -lt $TimeoutSeconds) {
        $status = & $CheckRunnerStatusBlock
        if ($status.IsRunning -eq $false) {
            $finished = $true
            $runnerExitCode = $status.ExitCode
            # M2: thu log trong try/catch — native stderr tren PS 5.1 (2>&1 +
            # EAP=Stop) co the nem NativeCommandError; loi thu log KHONG duoc
            # lam mat ket qua/exit code cua runner.
            try {
                $rawLog = & $GetRunnerLogsBlock
            } catch {
                Write-Warning "[RUNNER LOG] Khong the thu log cua runner (native stderr/PS 5.1): $_"
                $logFetchFailed = $true
            }
            break
        }
        if ($PollIntervalMs -gt 0) {
            Start-Sleep -Milliseconds $PollIntervalMs
        }
        $elapsed++
    }

    if (-not $finished) {
        Write-Warning "[RUNNER TIMEOUT] Runner vuot qua thoi han $TimeoutSeconds giay! Tien hanh dung an toan..."
        # Thu thap bang chung TRUOC khi dung runner (log + trang thai/exit code)
        # de khong mat dau vet dieu tra khi container bi xoa.
        $timeoutLog = $null
        try {
            $timeoutLog = & $GetRunnerLogsBlock
        } catch {
            Write-Warning "[RUNNER TIMEOUT] Khong the thu log truoc khi dung: $_"
            $logFetchFailed = $true
        }
        $stateBeforeStop = $null
        try { $stateBeforeStop = & $CheckRunnerStatusBlock } catch { }

        $isOwned = & $VerifyOwnershipBlock
        if ($isOwned -eq $true) {
            $stopResult = & $StopRunnerBlock
            if ($stopResult.Success -eq $false) {
                throw "[RUNNER STOP FAILED] Khong the dung runner container '$RunnerName': $($stopResult.Error)"
            }
            Write-Host "[RUNNER TIMEOUT] Da dung va giai phong runner container '$RunnerName' hop le."
        } else {
            throw "[OWNERSHIP MISMATCH] Khong xac dinh duoc quyen so huu cua runner container '$RunnerName'. TU CHOI DUNG DE TRANH ANH HUONG TIEN TRINH KHAC."
        }

        # F1: Chi tra exit code thuc te khi co BANG CHUNG container da thoat
        # (IsRunning=false) va exit code duoc thu thap TRUOC khi dung/xoa.
        # Neu container van dang chay (docker inspect tra State.Running=true
        # kem ExitCode=0 mac dinh) -> tra sentinel -1, khong duoc ghi "0".
        $exitCodeForTimeout = -1
        if ($null -ne $stateBeforeStop -and $stateBeforeStop.IsRunning -eq $false -and $null -ne $stateBeforeStop.ExitCode) {
            $exitCodeForTimeout = $stateBeforeStop.ExitCode
        }

        return @{
            Success        = $false
            Status         = "TimedOut"
            ExitCode       = $exitCodeForTimeout
            Elapsed        = $elapsed
            Log            = $timeoutLog
            LogFetchFailed = $logFetchFailed
            ErrorMessage   = "Runner execution timed out after $TimeoutSeconds seconds."
        }
    }

    return @{
        Success        = ($runnerExitCode -eq 0)
        Status         = if ($runnerExitCode -eq 0) { "CompletedSuccess" } else { "CompletedFailed" }
        ExitCode       = $runnerExitCode
        Elapsed        = $elapsed
        Log            = $rawLog
        LogFetchFailed = $logFetchFailed
        ErrorMessage   = if ($runnerExitCode -ne 0) { "Runner finished with non-zero exit code: $runnerExitCode" } else { $null }
    }
}
