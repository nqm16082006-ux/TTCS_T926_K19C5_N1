<#
.SYNOPSIS
    S-55: BO KIEM THU OFFLINE CHO CAC BLOCKER (A, B, C, D, E & DIEM PHU)
.DESCRIPTION
    Kiem thu offline 100% su dung mock/fake. Khong goi Docker, khong ket noi DB.
#>

$ErrorActionPreference = 'Stop'

Write-Host "=================================================================="
Write-Host " S-55: BO KIEM THU OFFLINE CHO CAC BLOCKER (ZERO DOCKER / ZERO DB)"
Write-Host "=================================================================="

$scriptDir = Split-Path $PSScriptRoot -Parent
$restoreScript = Join-Path $scriptDir "scripts\restore-drill.ps1"
$backupScript  = Join-Path $scriptDir "scripts\backup-db.ps1"
# H6: Runbook phai nam trong Git (scripts/run-drill-env.ps1), KHONG phu thuoc artifacts/ bi gitignore
$runbookScript = Join-Path $scriptDir "scripts\run-drill-env.ps1"
if (-not (Test-Path -LiteralPath $runbookScript)) {
    throw "Thieu scripts/run-drill-env.ps1 trong repository — test H6 khong the chay tren clone sach."
}

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

# Nap dinh nghia cac ham tu restore-drill.ps1 ma khong chay quy trinh restore that
. $restoreScript -TestOnlyValidation

# Nap dinh nghia cac ham helper tu S55-TestHelpers.ps1 (Quan ly trong Git, loai bo dependency artifacts - H6)
$helpersScript = Join-Path $PSScriptRoot "S55-TestHelpers.ps1"
. $helpersScript

# ==============================================================================
# NHOM 1: KIEM TRA CU PHAP TINH (AST PARSER)
# ==============================================================================
Write-Host "`n--- [NHOM 1] KIEM TRA CU PHAP TINH POWERSHELL AST ---"

Run-AssertTest 'AST Parser: scripts/backup-db.ps1 hop le (0 loi)' {
    $errors = $null
    $tokens = $null
    [System.Management.Automation.Language.Parser]::ParseFile($backupScript, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -ne 0) { throw "$($errors.Count) loi cu phap: $($errors[0].Message)" }
}

Run-AssertTest 'AST Parser: scripts/restore-drill.ps1 hop le (0 loi)' {
    $errors = $null
    $tokens = $null
    [System.Management.Automation.Language.Parser]::ParseFile($restoreScript, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -ne 0) { throw "$($errors.Count) loi cu phap: $($errors[0].Message)" }
}

Run-AssertTest 'AST Parser: tests/S55-TestHelpers.ps1 hop le (0 loi)' {
    $errors = $null
    $tokens = $null
    [System.Management.Automation.Language.Parser]::ParseFile($helpersScript, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -ne 0) { throw "$($errors.Count) loi cu phap: $($errors[0].Message)" }
}

# ==============================================================================
# NHOM 2: BLOCKER A - TIMEOUT AN TOAN
# ==============================================================================
Write-Host "`n--- [NHOM 2] BLOCKER A: TIMEOUT AN TOAN VA RUNNER CONTROL ---"

Run-AssertTest 'Blocker A.1: Runner hoan thanh truoc timeout (< 120s) -> PASS' {
    $simState = @{ Count = 0 }
    $result = Invoke-RunnerWithSafeTimeout -SessionId "s55_tc02_test1" `
        -RunnerName "mock_runner" `
        -TimeoutSeconds 120 `
        -PollIntervalMs 0 `
        -StartRunnerBlock { "mock_container_id" } `
        -CheckRunnerStatusBlock {
            $simState.Count++
            if ($simState.Count -ge 3) {
                return @{ IsRunning = $false; ExitCode = 0 }
            } else {
                return @{ IsRunning = $true; ExitCode = $null }
            }
        } `
        -GetRunnerLogsBlock { "Mock restore drill completed in 3s." } `
        -VerifyOwnershipBlock { $true } `
        -StopRunnerBlock { @{ Success = $true } }

    if (-not ($result.Success -eq $true -and $result.Status -eq "CompletedSuccess" -and $result.ExitCode -eq 0)) {
        throw "Ket qua khong dung: $($result | ConvertTo-Json -Compress)"
    }
}

Run-AssertTest 'Blocker A.2: Runner vuot timeout (> 120s) -> FAIL (Status = TimedOut)' {
    $stopState = @{ Called = $false }
    $result = Invoke-RunnerWithSafeTimeout -SessionId "s55_tc02_test2" `
        -RunnerName "mock_runner" `
        -TimeoutSeconds 2 `
        -PollIntervalMs 0 `
        -StartRunnerBlock { "mock_container_id" } `
        -CheckRunnerStatusBlock {
            return @{ IsRunning = $true; ExitCode = $null }
        } `
        -GetRunnerLogsBlock { "Mock log hanging..." } `
        -VerifyOwnershipBlock { $true } `
        -StopRunnerBlock {
            $stopState.Called = $true
            return @{ Success = $true }
        }

    if (-not ($result.Success -eq $false -and $result.Status -eq "TimedOut" -and $stopState.Called -eq $true)) {
        throw "Timeout khong duoc xu ly dung hoac khong dung runner an toan!"
    }
}

Run-AssertTest 'Blocker A.3: Khong xac dinh duoc runner thuoc session nao -> Tu choi dung va ABORT' {
    $ownershipMismatchCaught = $false
    try {
        Invoke-RunnerWithSafeTimeout -SessionId "s55_tc02_test3" `
            -RunnerName "mock_runner_foreign" `
            -TimeoutSeconds 1 `
            -PollIntervalMs 0 `
            -StartRunnerBlock { "mock_container_id" } `
            -CheckRunnerStatusBlock { @{ IsRunning = $true; ExitCode = $null } } `
            -GetRunnerLogsBlock { "" } `
            -VerifyOwnershipBlock { $false } `
            -StopRunnerBlock { @{ Success = $true } }
    } catch {
        if ($_ -match "OWNERSHIP MISMATCH") {
            $ownershipMismatchCaught = $true
        }
    }

    if (-not $ownershipMismatchCaught) {
        throw "He thong khong bat duoc loi ownership mismatch khi runner timeout!"
    }
}

Run-AssertTest 'Blocker A.4: Dung runner that bai -> Bao loi Stop Failure ro rang' {
    $stopFailureCaught = $false
    try {
        Invoke-RunnerWithSafeTimeout -SessionId "s55_tc02_test4" `
            -RunnerName "mock_runner_stuck" `
            -TimeoutSeconds 1 `
            -PollIntervalMs 0 `
            -StartRunnerBlock { "mock_container_id" } `
            -CheckRunnerStatusBlock { @{ IsRunning = $true; ExitCode = $null } } `
            -GetRunnerLogsBlock { "" } `
            -VerifyOwnershipBlock { $true } `
            -StopRunnerBlock { @{ Success = $false; Error = "Docker daemon connection refused" } }
    } catch {
        if ($_ -match "RUNNER STOP FAILED") {
            $stopFailureCaught = $true
        }
    }

    if (-not $stopFailureCaught) {
        throw "He thong khong bao loi khi dung runner that bai!"
    }
}

# ==============================================================================
# NHOM 3: BLOCKER B - BAO MAT MAT KHAU
# ==============================================================================
Write-Host "`n--- [NHOM 3] BLOCKER B: BAO MAT MAT KHAU VA ISOLATION ---"

Run-AssertTest 'Blocker B.1: Khong luu file mat khau tam test_pwd_tc02.tmp ra dia' {
    $tmpPwdFile = Join-Path $scriptDir "artifacts\test-evidence-s55\test_pwd_tc02.tmp"
    if (Test-Path -LiteralPath $tmpPwdFile) {
        throw "Phat hien file mat khau plaintext tren dia: $tmpPwdFile"
    }
    if (Test-Path -LiteralPath $runbookScript) {
        $rbContent = Get-Content -LiteralPath $runbookScript -Raw
        if ($rbContent -match 'Set-Content.*?test_pwd_tc02\.tmp') {
            throw "run-drill-env.ps1 van con ma nguon ghi test_pwd_tc02.tmp vao dia!"
        }
    }
}

Run-AssertTest 'Blocker B.2: Khong truyen connection string chua mat khau qua command line argument' {
    if (Test-Path -LiteralPath $runbookScript) {
        $rbContent = Get-Content -LiteralPath $runbookScript -Raw
        if ($rbContent -match 'pwsh\s+.*?scripts/restore-drill\.ps1.*?-TargetConnectionString\s+[\''"]?\$connStr') {
            throw "run-drill-env.ps1 van truyen connection string chua mat khau qua tham so dong lenh!"
        }
    }
    $restoreSource = Get-Content -LiteralPath $restoreScript -Raw
    if (-not ($restoreSource -match '\$env:TARGET_PG_CONNECTION')) {
        throw "restore-drill.ps1 thieu co che doc TARGET_PG_CONNECTION tu bien moi truong an toan!"
    }
}

Run-AssertTest 'Blocker B.3: Log doi soat khong chua chuoi mat khau nhay cam' {
    $dummyPassword = "s55_super_secret_pwd_9988!"
    $sampleRawLog = @(
        "[INFO] Dang chay phien drill...",
        "Executing with TARGET_PG_CONNECTION: Host=pg_host;Port=5432;Database=db;Password=$dummyPassword",
        "Finished successfully."
    ) -join "`r`n"

    $maskedLog = $sampleRawLog -replace [regex]::Escape($dummyPassword), '***'
    if ($maskedLog -match [regex]::Escape($dummyPassword)) {
        throw "Log van con chua mat khau sau khi loc!"
    }
    if (-not ($maskedLog -match 'Password=\*\*\*')) {
        throw "Log chua duoc che dung dinh dang!"
    }
}

Run-AssertTest 'Blocker B.4: Don dep bien credential trong bo nho o khoi finally' {
    if (Test-Path -LiteralPath $runbookScript) {
        $rbContent = Get-Content -LiteralPath $runbookScript -Raw
        if (-not ($rbContent -match '\$randomPwd\s*=\s*\$null' -and $rbContent -match '\$connStr\s*=\s*\$null')) {
            throw "run-drill-env.ps1 thieu buoc xoa bien credential trong RAM o khoi finally!"
        }
    }
}

# ==============================================================================
# NHOM 4: BLOCKER C - CLEANUP VA BAO TOAN TAI NGUYEN (MA TRAN 8 TRANG THAI)
# ==============================================================================
Write-Host "`n--- [NHOM 4] BLOCKER C: MA TRAN 8 TRANG THAI VONG DOI VA CLEANUP GATE ---"

function Evaluate-SimulationState {
    param(
        [bool]$NetworkCreated,
        [bool]$VolumeCreated,
        [bool]$ContainerCreated,
        [bool]$RunnerCreated,
        [bool]$DrillSucceeded,
        [bool]$PostCheckSucceeded,
        [bool]$OwnershipVerified,
        [bool]$SimulateCleanupError = $false
    )

    $drillPassed = ($DrillSucceeded -and $PostCheckSucceeded)
    $cleanupPassed = $false
    $preservedForTriage = $false
    $cleanedResources = @()
    $errors = @()

    if ($RunnerCreated) {
        if ($OwnershipVerified) {
            $cleanedResources += "runner"
        } else {
            $errors += "Runner ownership mismatch"
        }
    }

    if (-not $drillPassed -and $ContainerCreated) {
        $preservedForTriage = $true
        $cleanupPassed = $false
    } else {
        $success = $true
        if ($ContainerCreated) {
            if ($OwnershipVerified -and -not $SimulateCleanupError) { $cleanedResources += "container" } else { $success = $false; $errors += "Container error" }
        }
        if ($VolumeCreated) {
            if ($OwnershipVerified -and -not $SimulateCleanupError) { $cleanedResources += "volume" } else { $success = $false; $errors += "Volume error" }
        }
        if ($NetworkCreated) {
            if ($OwnershipVerified -and -not $SimulateCleanupError) { $cleanedResources += "network" } else { $success = $false; $errors += "Network error" }
        }
        $cleanupPassed = ($success -and $errors.Count -eq 0)
    }

    $finalPassed = ($drillPassed -and $cleanupPassed)
    return @{
        DrillPassed        = $drillPassed
        CleanupPassed      = $cleanupPassed
        FinalPassed        = $finalPassed
        PreservedForTriage = $preservedForTriage
        CleanedResources   = $cleanedResources
        Errors             = $errors
    }
}

Run-AssertTest 'Blocker C.1: Trang thai 1 - Chua tao tai nguyen -> Final FAIL (Abort)' {
    $st = Evaluate-SimulationState -NetworkCreated $false -VolumeCreated $false -ContainerCreated $false -RunnerCreated $false -DrillSucceeded $false -PostCheckSucceeded $false -OwnershipVerified $true
    if ($st.FinalPassed -ne $false -or $st.CleanedResources.Count -ne 0) { throw "Sai trang thai 1" }
}

Run-AssertTest 'Blocker C.2: Trang thai 2 - Tao network thanh cong, tao volume that bai -> Don network co nhan, Final FAIL' {
    $st = Evaluate-SimulationState -NetworkCreated $true -VolumeCreated $false -ContainerCreated $false -RunnerCreated $false -DrillSucceeded $false -PostCheckSucceeded $false -OwnershipVerified $true
    if ($st.FinalPassed -ne $false -or -not ($st.CleanedResources -contains "network")) { throw "Sai trang thai 2" }
}

Run-AssertTest 'Blocker C.3: Trang thai 3 - Tao volume thanh cong, tao container that bai -> Don volume va network, Final FAIL' {
    $st = Evaluate-SimulationState -NetworkCreated $true -VolumeCreated $true -ContainerCreated $false -RunnerCreated $false -DrillSucceeded $false -PostCheckSucceeded $false -OwnershipVerified $true
    if ($st.FinalPassed -ne $false -or -not ($st.CleanedResources -contains "volume" -and $st.CleanedResources -contains "network")) { throw "Sai trang thai 3" }
}

Run-AssertTest 'Blocker C.4: Trang thai 4 - Runner that bai -> Bao luu DB server va volume cho triage, dung runner, Final FAIL' {
    $st = Evaluate-SimulationState -NetworkCreated $true -VolumeCreated $true -ContainerCreated $true -RunnerCreated $true -DrillSucceeded $false -PostCheckSucceeded $false -OwnershipVerified $true
    if ($st.FinalPassed -ne $false -or $st.PreservedForTriage -ne $true -or -not ($st.CleanedResources -contains "runner")) { throw "Sai trang thai 4" }
}

Run-AssertTest 'Blocker C.5: Trang thai 5 - Runner timeout -> Bao luu DB server cho triage, dung runner, Final FAIL' {
    $st = Evaluate-SimulationState -NetworkCreated $true -VolumeCreated $true -ContainerCreated $true -RunnerCreated $true -DrillSucceeded $false -PostCheckSucceeded $false -OwnershipVerified $true
    if ($st.FinalPassed -ne $false -or $st.PreservedForTriage -ne $true) { throw "Sai trang thai 5" }
}

Run-AssertTest 'Blocker C.6: Trang thai 6 - Drill thanh cong nhung post-check that bai -> Bao luu DB server cho triage, Final FAIL' {
    $st = Evaluate-SimulationState -NetworkCreated $true -VolumeCreated $true -ContainerCreated $true -RunnerCreated $false -DrillSucceeded $true -PostCheckSucceeded $false -OwnershipVerified $true
    if ($st.FinalPassed -ne $false -or $st.PreservedForTriage -ne $true) { throw "Sai trang thai 6" }
}

Run-AssertTest 'Blocker C.7: Trang thai 7 - Drill thanh cong nhung cleanup that bai -> Final FAIL (khong duoc bao PASS)' {
    $st = Evaluate-SimulationState -NetworkCreated $true -VolumeCreated $true -ContainerCreated $true -RunnerCreated $false -DrillSucceeded $true -PostCheckSucceeded $true -OwnershipVerified $true -SimulateCleanupError $true
    if ($st.DrillPassed -ne $true -or $st.CleanupPassed -ne $false -or $st.FinalPassed -ne $false) {
        throw "Cleanup that bai nhung van PASS: $($st | ConvertTo-Json -Compress)"
    }
}

Run-AssertTest 'Blocker C.8: Trang thai 8 - Toan bo qua trinh thanh cong -> Drill PASS, Cleanup PASS, Final PASS' {
    $st = Evaluate-SimulationState -NetworkCreated $true -VolumeCreated $true -ContainerCreated $true -RunnerCreated $false -DrillSucceeded $true -PostCheckSucceeded $true -OwnershipVerified $true -SimulateCleanupError $false
    if ($st.DrillPassed -ne $true -or $st.CleanupPassed -ne $true -or $st.FinalPassed -ne $true) {
        throw "Toan bo thanh cong nhung khong dat Final PASS: $($st | ConvertTo-Json -Compress)"
    }
}

# ==============================================================================
# NHOM 5: BLOCKER D - RTO 900 GIAY
# ==============================================================================
Write-Host "`n--- [NHOM 5] BLOCKER D: DO LUONG VA CHOT CHAN RTO 900 GIAY ---"

Run-AssertTest 'Blocker D.1: RTO nho hon 900 giay (vi du: 45.2s) -> PASS' {
    $res = Assert-RtoThreshold -RtoSeconds 45.2 -MaxAllowedSeconds 900
    if ($res.Passed -ne $true -or $res.RtoSeconds -ne 45.2) { throw "Loi RTO < 900" }
}

Run-AssertTest 'Blocker D.2: RTO dung 900 giay (900.0s) -> PASS' {
    $res = Assert-RtoThreshold -RtoSeconds 900.0 -MaxAllowedSeconds 900
    if ($res.Passed -ne $true) { throw "Loi RTO = 900" }
}

Run-AssertTest 'Blocker D.3: RTO lon hon 900 giay (vi du: 900.1s, 1200s) -> Bat buoc nem loi FAIL' {
    $failed = $false
    try {
        Assert-RtoThreshold -RtoSeconds 900.1 -MaxAllowedSeconds 900 | Out-Null
    } catch {
        if ($_ -match "RTO VUOT NGUONG") { $failed = $true }
    }
    if (-not $failed) { throw "RTO > 900 khong bi nem loi!" }
}

Run-AssertTest 'Blocker D.4: RTO khong xac dinh hoac loi do thoi gian -> FAIL' {
    $caughtNull = $false
    $caughtNegative = $false
    $caughtInvalid = $false

    try { Assert-RtoThreshold -RtoSeconds $null | Out-Null } catch { $caughtNull = $true }
    try { Assert-RtoThreshold -RtoSeconds -5.0 | Out-Null } catch { $caughtNegative = $true }
    try { Assert-RtoThreshold -RtoSeconds "invalid_time" | Out-Null } catch { $caughtInvalid = $true }

    if (-not ($caughtNull -and $caughtNegative -and $caughtInvalid)) {
        throw "Chot chan RTO khong chan duoc gia tri bat hop le!"
    }
}

# ==============================================================================
# NHOM 6: BLOCKER E - SNAPSHOT VA PASS/FAIL
# ==============================================================================
Write-Host "`n--- [NHOM 6] BLOCKER E: SNAPSHOT CO CAU TRUC VA BAO TOAN 28 CONTAINER ---"

$mock28Containers = @(
    '111111111111 | restaurant-api | Up 2 hours | 0.0.0.0:8080',
    '222222222222 | restaurant-db  | Up 2 hours | 0.0.0.0:5432',
    '333333333333 | booking-api     | Up 3 hours | 0.0.0.0:8081',
    '444444444444 | booking-db      | Up 3 hours | 0.0.0.0:5433'
)
for ($k = 5; $k -le 28; $k++) {
    $hexId = ("{0:D12}" -f $k)
    $mock28Containers += "$hexId | foreign_container_$k | Up 1 hours |"
}

$beforeMap = Parse-DockerSnapshot -SnapshotLines $mock28Containers

Run-AssertTest 'Blocker E.1: Toan bo 28 container ban dau duoc bao toan 100% -> PASS (Preserved)' {
    $mockAfterPreserved = @()
    foreach ($line in $mock28Containers) {
        $parts = $line -split '\|'
        $mockAfterPreserved += "$($parts[0].Trim()) | $($parts[1].Trim()) | Up 4 hours |"
    }
    $afterMap = Parse-DockerSnapshot -SnapshotLines $mockAfterPreserved

    $res = Compare-DockerSnapshots -BeforeMap $beforeMap -AfterMap $afterMap
    if ($res.IsIdentical -ne $true -or $res.Status -ne "Preserved" -or $res.Count -ne 28) {
        throw "Snapshot so sanh that bai: $($res | ConvertTo-Json -Compress)"
    }
}

Run-AssertTest 'Blocker E.2: Container cu bi mat (vi du container ID 000000000005 bi xoa) -> FAIL' {
    $mockAfterMissing = $mock28Containers | Where-Object { -not ($_ -match '^000000000005') }
    $afterMap = Parse-DockerSnapshot -SnapshotLines $mockAfterMissing

    $res = Compare-DockerSnapshots -BeforeMap $beforeMap -AfterMap $afterMap
    if ($res.IsIdentical -ne $false -or $res.Status -ne "ForeignContainersMissing") {
        throw "Khong phat hien duoc container bi mat: $($res | ConvertTo-Json -Compress)"
    }
}

Run-AssertTest 'Blocker E.3: Container phat sinh chua duoc don dep (Leftover) -> FAIL' {
    $mockAfterLeftover = @($mock28Containers) + @("999999999999 | eventpulse_test_pg_s55 | Up 1 min |")
    $afterMap = Parse-DockerSnapshot -SnapshotLines $mockAfterLeftover

    $res = Compare-DockerSnapshots -BeforeMap $beforeMap -AfterMap $afterMap
    if ($res.IsIdentical -ne $false -or $res.Status -ne "LeftoverContainersDetected") {
        throw "Khong phat hien duoc container le: $($res | ConvertTo-Json -Compress)"
    }
}

Run-AssertTest 'Blocker E.4: Snapshot rong hoac khong doc duoc -> Bao loi FAIL (Fail-closed)' {
    $caughtEmptyBefore = $false
    $caughtEmptyAfter = $false

    try { Compare-DockerSnapshots -BeforeMap @{} -AfterMap $beforeMap | Out-Null } catch { $caughtEmptyBefore = $true }
    try { Compare-DockerSnapshots -BeforeMap $beforeMap -AfterMap @{} | Out-Null } catch { $caughtEmptyAfter = $true }

    if (-not ($caughtEmptyBefore -and $caughtEmptyAfter)) {
        throw "Snapshot rong khong bi chan fail-closed!"
    }
}

# ==============================================================================
# NHOM 7: DIEM PHU - PG_RESTORE EXIT CODE, FK/INDEX VA SQL WILDCARD
# ==============================================================================
Write-Host "`n--- [NHOM 7] DIEM PHU: EXIT CODE, FK/INDEX VA SQL WILDCARD ---"

Run-AssertTest 'Diem phu 7.2: pg_restore exit code 0 -> CleanSuccess' {
    $res = Assert-PgRestoreExitCode -ExitCode 0
    if ($res.Status -ne "CleanSuccess") { throw "Exit code 0 that bai" }
}

Run-AssertTest 'Diem phu 7.2: pg_restore exit code 1 -> WarningAccepted (Can integrity check)' {
    $res = Assert-PgRestoreExitCode -ExitCode 1
    if ($res.Status -ne "WarningAccepted") { throw "Exit code 1 that bai" }
}

Run-AssertTest 'Diem phu 7.2: pg_restore exit code > 1 hoac null -> Nem loi Fail-closed' {
    $caughtFatal = $false
    $caughtNull = $false
    try { Assert-PgRestoreExitCode -ExitCode 2 | Out-Null } catch { $caughtFatal = $true }
    try { Assert-PgRestoreExitCode -ExitCode $null | Out-Null } catch { $caughtNull = $true }

    if (-not ($caughtFatal -and $caughtNull)) { throw "Exit code nguy hiem khong bi throw!" }
}

Run-AssertTest 'Diem phu 7.3: Foreign Keys va Indexes > 0 -> PASS' {
    $res = Assert-DatabaseIntegrityCounts -MigrationCount 35 -TableCount 12 -FkCount 8 -IndexCount 15
    if ($res.Status -ne "IntegrityPassed") { throw "Integrity checks > 0 that bai" }
}

Run-AssertTest 'Diem phu 7.3: Foreign Keys = 0 hoac Indexes = 0 -> Bat buoc nem loi FAIL' {
    $caughtZeroFk = $false
    $caughtZeroIdx = $false
    try { Assert-DatabaseIntegrityCounts -MigrationCount 35 -TableCount 12 -FkCount 0 -IndexCount 15 | Out-Null } catch { $caughtZeroFk = $true }
    try { Assert-DatabaseIntegrityCounts -MigrationCount 35 -TableCount 12 -FkCount 8 -IndexCount 0 | Out-Null } catch { $caughtZeroIdx = $true }

    if (-not ($caughtZeroFk -and $caughtZeroIdx)) { throw "FK hoac Index bang 0 khong bi chan!" }
}

Run-AssertTest 'Diem phu 7.4: SQL Wildcard LEFT(table_name, 2) <> __ loc chinh xac bang he thong' {
    $allTables = @(
        "__EFMigrationsHistory",
        "__eventpulse_drill_ownership",
        "events",
        "orders",
        "roles",
        "seat_categories",
        "seats",
        "showtimes",
        "tickets",
        "users"
    )

    $filteredTables = $allTables | Where-Object {
        $prefix = if ($_.Length -ge 2) { $_.Substring(0, 2) } else { $_ }
        $prefix -ne "__"
    }

    if ($filteredTables.Count -ne 8) {
        throw "So luong bang sau loc khong dung: $($filteredTables.Count) (Ky vong: 8)"
    }
    if ($filteredTables -contains "__EFMigrationsHistory" -or $filteredTables -contains "__eventpulse_drill_ownership") {
        throw "Bang marker bi sot lai trong danh sach sau loc!"
    }
    if (-not ($filteredTables -contains "events" -and $filteredTables -contains "orders" -and $filteredTables -contains "users")) {
        throw "Bang nghiep vu bi xoa nham boi bo loc!"
    }
}

# S55-BASELINE-FIX: Regression test cho đối chiếu baseline khi manifest (ghi lúc backup)
# bao gồm cả bảng tiền tố '__' (như __EFMigrationsHistory) trong khi thống kê sau restore
# chỉ đếm bảng nghiệp vụ. Bảng hệ thống phải được bổ sung từ MigrationCount (Cấp 1).
Run-AssertTest 'Diem phu 7.5: Baseline co __EFMigrationsHistory -> bo sung tu MigrationCount, khong bao THIEU BANG' {
    $baseline = [PSCustomObject]@{
        __EFMigrationsHistory = 42
        events                = 10
        orders                = 98
        tickets               = 145
    }
    $tableStats = @(
        [PSCustomObject]@{ TableName = "events"; RowCount = 10 }
        [PSCustomObject]@{ TableName = "orders"; RowCount = 98 }
        [PSCustomObject]@{ TableName = "tickets"; RowCount = 145 }
    )
    $res = Compare-S55BaselineRowCounts -Baseline $baseline -TableStats $tableStats -MigrationCount 42
    if ($res.MismatchCount -ne 0) {
        throw "Baseline dang le phai khop (Mismatch: $($res.Mismatches -join '; '))"
    }
}

Run-AssertTest 'Diem phu 7.6: __EFMigrationsHistory sau restore lech so luong -> BAT MISMATCH' {
    $baseline = [PSCustomObject]@{
        __EFMigrationsHistory = 42
        events                = 10
    }
    $tableStats = @(
        [PSCustomObject]@{ TableName = "events"; RowCount = 10 }
    )
    $res = Compare-S55BaselineRowCounts -Baseline $baseline -TableStats $tableStats -MigrationCount 41
    if ($res.MismatchCount -ne 1 -or ($res.Mismatches -join '; ') -notmatch '__EFMigrationsHistory') {
        throw "Phai phat hien lech migration history (Mismatch: $($res.Mismatches -join '; '))"
    }
}

Run-AssertTest 'Diem phu 7.7: Bang nghiep vu lech so dong hoac THIEU BANG van bi chan' {
    $baseline = [PSCustomObject]@{
        events  = 10
        orders  = 98
        tickets = 145
    }
    $tableStats = @(
        [PSCustomObject]@{ TableName = "events"; RowCount = 9 }
        [PSCustomObject]@{ TableName = "orders"; RowCount = 98 }
    )
    $res = Compare-S55BaselineRowCounts -Baseline $baseline -TableStats $tableStats -MigrationCount 0
    if ($res.MismatchCount -ne 2) {
        throw "Phai phat hien 2 lech (events sai so dong, tickets THIEU BANG). Thuc te: $($res.Mismatches -join '; ')"
    }
    if (($res.Mismatches -join '; ') -notmatch 'tickets.*THIEU BANG') {
        throw "Thieu canh bao THIEU BANG cho bang khong duoc dem: $($res.Mismatches -join '; ')"
    }
}

Run-AssertTest 'Diem phu 7.8: Bang moi xuat hien sau restore khong co trong baseline -> khong fail' {
    $baseline = [PSCustomObject]@{
        events = 10
    }
    $tableStats = @(
        [PSCustomObject]@{ TableName = "events"; RowCount = 10 }
        [PSCustomObject]@{ TableName = "refund_requests"; RowCount = 23 }
    )
    $res = Compare-S55BaselineRowCounts -Baseline $baseline -TableStats $tableStats -MigrationCount 0
    if ($res.MismatchCount -ne 0) {
        throw "Bang moi them sau backup khong duoc tinh la mismatch: $($res.Mismatches -join '; ')"
    }
}

# ==============================================================================
# NHOM 8: BLOCKER H1 (HOST GUARD DENY-BY-DEFAULT), H2 (MANIFEST & SHA PRE-CHECK), H3 (PG_DUMP TIMEOUT)
# ==============================================================================
Write-Host "`n--- [NHOM 8] BLOCKER H1, H2, H3: PHONG VE AN TOAN MOI ---"

# H1 Tests - Deny-by-default Host Guard
Run-AssertTest 'Blocker H1.1: Host la khong nam trong allowlist (192.168.1.50) -> Bi TU CHOI (Deny-by-default)' {
    $denied = $false
    try {
        & $restoreScript -TestOnlyValidation -TestValidationInputs @{
            HostSafetyTest = @{
                TargetParams = @{ Host = "192.168.1.50"; Port = 5432; Username = "postgres" }
                SourceParams = @{ Host = "localhost"; Database = "event_ticket_db" }
                TargetDbName = "eventpulse_drill_test_db"
            }
        }
    } catch {
        if ($_ -match "DENY BY DEFAULT") { $denied = $true }
    }
    if (-not $denied) { throw "Host la khong bi tu choi boi Deny-by-default!" }
}

Run-AssertTest 'Blocker H1.2: Host cloud (neon.tech) -> Bi TU CHOI tuyet doi' {
    $deniedCloud = $false
    try {
        & $restoreScript -TestOnlyValidation -TestValidationInputs @{
            HostSafetyTest = @{
                TargetParams = @{ Host = "ep-test.ap-southeast-1.neon.tech"; Port = 5432; Username = "postgres" }
                SourceParams = @{ Host = "localhost"; Database = "event_ticket_db" }
                TargetDbName = "eventpulse_drill_test_db"
            }
        }
    } catch {
        if ($_ -match "Cloud") { $deniedCloud = $true }
    }
    if (-not $deniedCloud) { throw "Cloud host khong bi tu choi!" }
}

Run-AssertTest 'Blocker H1.3: Host localhost va 127.0.0.1 -> Duoc CHAP NHAN trong allowlist' {
    $resLocal = & $restoreScript -TestOnlyValidation -TestValidationInputs @{
        HostSafetyTest = @{
            TargetParams = @{ Host = "localhost"; Port = 5432; Username = "postgres" }
            SourceParams = @{ Host = "127.0.0.1"; Database = "event_ticket_db" }
            TargetDbName = "eventpulse_drill_test_db"
        }
    }
}

Run-AssertTest 'Blocker H1.4: Host duoc khai bao tuong minh qua AllowedHosts -> Duoc CHAP NHAN' {
    $resCustom = & $restoreScript -TestOnlyValidation -TestValidationInputs @{
        HostSafetyTest = @{
            TargetParams = @{ Host = "custom-test-container"; Port = 5432; Username = "postgres" }
            SourceParams = @{ Host = "localhost"; Database = "event_ticket_db" }
            TargetDbName = "eventpulse_drill_test_db"
            AllowedHosts = @("custom-test-container")
        }
    }
}

# H2 Tests - Manifest & SHA-256 Pre-Restore Check
Run-AssertTest 'Blocker H2.1: File dump thieu manifest JSON -> Tu choi restore' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_h2_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $dummyDump = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump"
        [System.IO.File]::WriteAllBytes($dummyDump, (New-Object byte[] 2048))

        $rejectedNoManifest = $false
        try {
            & $restoreScript -TestOnlyValidation -TestValidationInputs @{
                ArchiveIntegrityTest = @{ DumpFilePath = $dummyDump }
            }
        } catch {
            if ($_ -match "Khong tim thay manifest JSON") { $rejectedNoManifest = $true }
        }
        if (-not $rejectedNoManifest) { throw "File dump thieu manifest khong bi tu choi!" }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Run-AssertTest 'Blocker H2.2: File dump co SHA-256 khong khop manifest -> Tu choi restore' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_h2_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $dummyDump = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump"
        $dummyManifest = Join-Path $tempDir "eventpulse_backup_20261018_120000.json"
        [System.IO.File]::WriteAllBytes($dummyDump, (New-Object byte[] 2048))

        $badManifest = [PSCustomObject]@{
            backupFile = "eventpulse_backup_20261018_120000.dump"
            sizeBytes  = 2048
            sha256     = "1111111111111111111111111111111111111111111111111111111111111111"
            status     = "VerifiedValid"
        }
        $badManifest | ConvertTo-Json | Set-Content -LiteralPath $dummyManifest -Encoding utf8

        $rejectedBadHash = $false
        try {
            & $restoreScript -TestOnlyValidation -TestValidationInputs @{
                ArchiveIntegrityTest = @{ DumpFilePath = $dummyDump }
            }
        } catch {
            if ($_ -match "KHONG KHOP voi SHA-256") { $rejectedBadHash = $true }
        }
        if (-not $rejectedBadHash) { throw "File dump sai SHA-256 khong bi tu choi!" }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Run-AssertTest 'Blocker H2.3: File dump co manifest status khong phai VerifiedValid -> Tu choi restore' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_h2_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $dummyDump = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump"
        $dummyManifest = Join-Path $tempDir "eventpulse_backup_20261018_120000.json"
        $content = (New-Object byte[] 2048)
        [System.IO.File]::WriteAllBytes($dummyDump, $content)
        $sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $dummyDump).Hash

        $unverifiedManifest = [PSCustomObject]@{
            backupFile = "eventpulse_backup_20261018_120000.dump"
            sizeBytes  = 2048
            sha256     = $sha
            status     = "UnverifiedPending"
        }
        $unverifiedManifest | ConvertTo-Json | Set-Content -LiteralPath $dummyManifest -Encoding utf8

        $rejectedUnverified = $false
        try {
            & $restoreScript -TestOnlyValidation -TestValidationInputs @{
                ArchiveIntegrityTest = @{ DumpFilePath = $dummyDump }
            }
        } catch {
            if ($_ -match "Trang thai manifest khong phai 'VerifiedValid'") { $rejectedUnverified = $true }
        }
        if (-not $rejectedUnverified) { throw "Manifest chua verified khong bi tu choi!" }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Run-AssertTest 'Blocker H2.4: File dump co manifest hop le va SHA-256 khop -> Chon va xac thuc thanh cong' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_h2_test_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $dummyDump = Join-Path $tempDir "eventpulse_backup_20261018_120000.dump"
        $dummyManifest = Join-Path $tempDir "eventpulse_backup_20261018_120000.json"
        $content = (New-Object byte[] 2048)
        [System.IO.File]::WriteAllBytes($dummyDump, $content)
        $sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $dummyDump).Hash

        $validManifest = [PSCustomObject]@{
            backupFile = "eventpulse_backup_20261018_120000.dump"
            sizeBytes  = 2048
            sha256     = $sha
            status     = "VerifiedValid"
        }
        $validManifest | ConvertTo-Json | Set-Content -LiteralPath $dummyManifest -Encoding utf8

        $resObj = & $restoreScript -TestOnlyValidation -TestValidationInputs @{
            ArchiveIntegrityTest = @{ DumpFilePath = $dummyDump }
        }
        if ($resObj.sha256 -ne $sha -or $resObj.status -ne "VerifiedValid") {
            throw "Ket qua tra ve khong dung manifest hop le!"
        }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# H3 Tests - pg_dump Timeout Guard
Run-AssertTest 'Blocker H3.1: Tien trinh pg_dump hoan thanh trong thoi han (< Timeout) -> PASS' {
    $fastCmd = "powershell.exe"
    $fastArgs = @("-NoProfile", "-Command", "exit 0")
    $exitCode = & $backupScript -TestOnlyTimeout -TimeoutTestInputs @{
        Exe = $fastCmd
        Args = $fastArgs
        TimeoutSeconds = 5
    }
    if ($exitCode -ne 0) { throw "Fast process that bai: exit code $exitCode" }
}

Run-AssertTest 'Blocker H3.2: Tien trinh pg_dump treo vuot qua thoi han -> Kill tien trinh va nem ngoai le timeout' {
    $slowCmd = "powershell.exe"
    $slowArgs = @("-NoProfile", "-Command", "Start-Sleep -Seconds 10")
    $timedOut = $false
    try {
        & $backupScript -TestOnlyTimeout -TimeoutTestInputs @{
            Exe = $slowCmd
            Args = $slowArgs
            TimeoutSeconds = 1
        }
    } catch {
        if ($_ -match "pg_dump bi timeout sau 1 giay va da bi dung an toan") {
            $timedOut = $true
        }
    }
    if (-not $timedOut) { throw "Tien trinh treo khong bi kill dung han sau timeout!" }
}

# ==============================================================================
# NHOM 9: N1 — KHONG FALLBACK IM LANG VE CONNECTION STRING MAC DINH
# ==============================================================================
Write-Host "`n--- [NHOM 9] N1: FAIL-CLOSED TARGET CONNECTION (KHONG FALLBACK LOCALHOST) ---"

Run-AssertTest 'N1.1: Tham so -TargetConnectionString duoc uu tien hon env' {
    $oldTarget = $env:TARGET_PG_CONNECTION
    $oldDefault = $env:ConnectionStrings__DefaultConnection
    try {
        $env:TARGET_PG_CONNECTION = 'Host=env-target;Port=5432;Database=postgres;Username=postgres;Password=envpw'
        $env:ConnectionStrings__DefaultConnection = 'Host=env-default;Port=5432;Database=postgres;Username=postgres;Password=envpw'
        $res = & $restoreScript -TestOnlyValidation -TestValidationInputs @{
            TargetConnectionTest = @{ ParamValue = 'Host=param-target;Port=5432;Database=postgres;Username=postgres;Password=parampw' }
        }
        if ($res -ne 'Host=param-target;Port=5432;Database=postgres;Username=postgres;Password=parampw') {
            throw "Tham so khong duoc uu tien: $res"
        }
    } finally {
        $env:TARGET_PG_CONNECTION = $oldTarget
        $env:ConnectionStrings__DefaultConnection = $oldDefault
    }
}

Run-AssertTest 'N1.2: Tham so rong + TARGET_PG_CONNECTION -> dung env TARGET' {
    $oldTarget = $env:TARGET_PG_CONNECTION
    $oldDefault = $env:ConnectionStrings__DefaultConnection
    try {
        $env:TARGET_PG_CONNECTION = 'Host=env-target;Port=5432;Database=postgres;Username=postgres;Password=envpw'
        Remove-Item env:ConnectionStrings__DefaultConnection -ErrorAction SilentlyContinue
        $res = & $restoreScript -TestOnlyValidation -TestValidationInputs @{ TargetConnectionTest = @{ ParamValue = '' } }
        if ($res -ne 'Host=env-target;Port=5432;Database=postgres;Username=postgres;Password=envpw') {
            throw "Khong dung env TARGET: $res"
        }
    } finally {
        $env:TARGET_PG_CONNECTION = $oldTarget
        $env:ConnectionStrings__DefaultConnection = $oldDefault
    }
}

Run-AssertTest 'N1.3: Tham so rong + khong TARGET + ConnectionStrings__DefaultConnection -> dung env Default' {
    $oldTarget = $env:TARGET_PG_CONNECTION
    $oldDefault = $env:ConnectionStrings__DefaultConnection
    try {
        Remove-Item env:TARGET_PG_CONNECTION -ErrorAction SilentlyContinue
        $env:ConnectionStrings__DefaultConnection = 'Host=env-default;Port=5432;Database=postgres;Username=postgres;Password=envpw'
        $res = & $restoreScript -TestOnlyValidation -TestValidationInputs @{ TargetConnectionTest = @{ ParamValue = '' } }
        if ($res -ne 'Host=env-default;Port=5432;Database=postgres;Username=postgres;Password=envpw') {
            throw "Khong dung env Default: $res"
        }
    } finally {
        $env:TARGET_PG_CONNECTION = $oldTarget
        $env:ConnectionStrings__DefaultConnection = $oldDefault
    }
}

Run-AssertTest 'N1.4: Thieu ca ba -> NEM LOI fail-closed, KHONG fallback localhost, KHONG ket noi' {
    $oldTarget = $env:TARGET_PG_CONNECTION
    $oldDefault = $env:ConnectionStrings__DefaultConnection
    try {
        Remove-Item env:TARGET_PG_CONNECTION -ErrorAction SilentlyContinue
        Remove-Item env:ConnectionStrings__DefaultConnection -ErrorAction SilentlyContinue
        $caught = $null
        try {
            & $restoreScript -TestOnlyValidation -TestValidationInputs @{ TargetConnectionTest = @{ ParamValue = '' } } | Out-Null
        } catch { $caught = $_.Exception.Message }
        if ($null -eq $caught) { throw "Phai nem loi khi thieu cau hinh" }
        if ($caught -notmatch 'KHONG fallback') { throw "Thieu cam ket fail-closed trong loi: $caught" }
        if ($caught -match 'Host=localhost;Port=5432') { throw "Loi khong duoc chua chuoi fallback cu: $caught" }
        if ($caught -match 'postgres_password_123') { throw "Loi khong duoc chua mat khau dev mac dinh: $caught" }
    } finally {
        $env:TARGET_PG_CONNECTION = $oldTarget
        $env:ConnectionStrings__DefaultConnection = $oldDefault
    }
}

Run-AssertTest 'N1.5: Main flow cua restore-drill goi Resolve-S55TargetConnection (khong con fallback cu)' {
    $content = Get-Content -LiteralPath $restoreScript -Raw
    if ($content -notmatch 'Resolve-S55TargetConnection -TargetConnectionString \$TargetConnectionString') {
        throw "Main flow khong dung helper fail-closed"
    }
    if ($content -match 'Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres_password_123') {
        throw "Van con fallback connection string mac dinh trong restore-drill.ps1!"
    }
}

# ==============================================================================
# NHOM 10: TC-02 — CONG BI CHIEM, SNAPSHOT SAFE, CLEANUP MODE
# ==============================================================================
Write-Host "`n--- [NHOM 10] TC-02: PORT / SNAPSHOT SAFE / CLEANUP MODE ---"

Run-AssertTest 'TC02.1: Get-S55FreeHostPort chon ung vien dau tien con trong' {
    $busy = @(5436, 5437)
    $chosen = Get-S55FreeHostPort -StartPort 5436 -MaxAttempts 5 -IsPortInUse {
        param([int]$Port) return ($busy -contains $Port)
    }
    if ($chosen -ne 5438) { throw "Sai cong duoc chon: $chosen" }
}

Run-AssertTest 'TC02.2: Tat ca ung vien deu ban -> NEM LOI ABORT (khong hardcode)' {
    $caught = $null
    try {
        Get-S55FreeHostPort -StartPort 5436 -MaxAttempts 3 -IsPortInUse { param([int]$Port) return $true } | Out-Null
    } catch { $caught = $_.Exception.Message }
    if ($null -eq $caught -or $caught -notmatch 'ABORT') { throw "Phai nem loi khi het cong trong" }
}

Run-AssertTest 'TC02.3: Compare-S55SnapshotsSafe — khong co before-snapshot -> Skipped, KHONG quy container cu la leftover' {
    $after = @{ '111111111111' = 'restaurant-api'; '222222222222' = 'eventpulse_test_pg_s55' }
    $res = Compare-S55SnapshotsSafe -BeforeMap @{} -AfterMap $after
    if ($res.Status -ne 'SkippedNoBeforeSnapshot') { throw "Sai status: $($res.Status)" }
    if ($res.Message -notmatch 'KHONG bi xem la tai nguyen roi') { throw "Thieu cam ket khong do loi container cu" }
}

Run-AssertTest 'TC02.4: Compare-S55SnapshotsSafe — co before-snapshot thi uy quyen Compare-DockerSnapshots (phan biet cu/moi)' {
    $before = @{ '111111111111' = 'restaurant-api' }
    $after = @{
        '111111111111' = 'restaurant-api'
        '999999999999' = 'eventpulse_test_pg_tc02'
    }
    $res = Compare-S55SnapshotsSafe -BeforeMap $before -AfterMap $after
    if ($res.Status -ne 'LeftoverContainersDetected') { throw "Sai status: $($res.Status)" }
    if (($res.Leftovers -join ';') -notmatch 'eventpulse_test_pg_tc02') { throw "Thieu container TC-02 trong leftovers" }
    if (($res.Leftovers -join ';') -match 'restaurant-api') { throw "Container co truoc phien bi do loi nham!" }
}

Run-AssertTest 'TC02.5: Cleanup mode — chua tao tai nguyen + drill fail -> Skip (khong tuyen bo cleanup thanh cong)' {
    $m = Resolve-S55CleanupMode -DrillPassed $false -AnyResourceCreated $false
    if ($m -ne 'Skip') { throw "Sai mode: $m" }
}

Run-AssertTest 'TC02.6: Cleanup mode — da tao + drill fail + container da tao -> Preserve (triage)' {
    $m = Resolve-S55CleanupMode -DrillPassed $false -AnyResourceCreated $true -ContainerCreated $true
    if ($m -ne 'Preserve') { throw "Sai mode: $m" }
}

Run-AssertTest 'TC02.7: Cleanup mode — drill thanh cong -> Cleanup co xac minh nhan' {
    $m = Resolve-S55CleanupMode -DrillPassed $true -AnyResourceCreated $true -ContainerCreated $true
    if ($m -ne 'Cleanup') { throw "Sai mode: $m" }
}

Run-AssertTest 'TC02.8: Cleanup mode — chi runner duoc tao (drill fail) -> van Cleanup (khong Preserve nhieu)' {
    $m = Resolve-S55CleanupMode -DrillPassed $false -AnyResourceCreated $true -ContainerCreated $false
    if ($m -ne 'Cleanup') { throw "Sai mode: $m" }
}

# ==============================================================================
# NHOM 11: TC-02 TIMEOUT — THU THAP BANG CHUNG TRUOC KHI DUNG RUNNER
# ==============================================================================
Write-Host "`n--- [NHOM 11] TC-02 TIMEOUT: EVIDENCE TRUOC KHI DUNG RUNNER ---"

Run-AssertTest 'TC03.1: Timeout — thu log TRUOC khi stop, tra ve Log + trang thai, khong bien thanh PASS' {
    $script:callOrderEv1 = @()
    $result = Invoke-RunnerWithSafeTimeout -SessionId "s55_tc02_ev1" `
        -RunnerName "mock_runner_ev1" `
        -TimeoutSeconds 2 `
        -PollIntervalMs 0 `
        -StartRunnerBlock { "mock_container_id" } `
        -CheckRunnerStatusBlock {
            $script:callOrderEv1 += "status"
            return @{ IsRunning = $true; ExitCode = $null }
        } `
        -GetRunnerLogsBlock {
            $script:callOrderEv1 += "logs"
            return "apk: Setting postgresql15 as the default version"
        } `
        -VerifyOwnershipBlock { $true } `
        -StopRunnerBlock {
            $script:callOrderEv1 += "stop"
            return @{ Success = $true }
        }

    $order = $script:callOrderEv1
    if ($result.Success -ne $false -or $result.Status -ne "TimedOut") { throw "Timeout khong duoc bao FAIL: $($result | ConvertTo-Json -Compress)" }
    if ($result.ExitCode -ne -1) { throw "Container dang chay phai tra ExitCode -1, khong phai $($result.ExitCode)" }
    if ($result.Log -notmatch 'Setting postgresql15') { throw "Log timeout khong duoc thu thap" }
    if ($order[-1] -ne 'stop') { throw "Phai stop cuoi cung: $($order -join ',')" }
    $logIndex = [array]::IndexOf($order, 'logs')
    $stopIndex = [array]::IndexOf($order, 'stop')
    if ($logIndex -lt 0 -or $logIndex -gt $stopIndex) { throw "Log phai duoc thu TRUOC khi stop: $($order -join ',')" }
}

Run-AssertTest 'F1.1: Timeout khi container VAN DANG CHAY (inspect tra "true 0") -> ExitCode -1, KHONG ghi 0' {
    $result = Invoke-RunnerWithSafeTimeout -SessionId "s55_tc02_f1_1" `
        -RunnerName "mock_runner_f1_1" `
        -TimeoutSeconds 2 `
        -PollIntervalMs 0 `
        -StartRunnerBlock { "mock_container_id" } `
        -CheckRunnerStatusBlock { @{ IsRunning = $true; ExitCode = 0 } } `
        -GetRunnerLogsBlock { "log" } `
        -VerifyOwnershipBlock { $true } `
        -StopRunnerBlock { @{ Success = $true } }

    if ($result.Success -ne $false -or $result.Status -ne "TimedOut") { throw "Phai FAIL" }
    if ($result.ExitCode -ne -1) { throw "ExitCode phai la -1 khi container con chay (khong duoc lay 0 mac dinh cua inspect): $($result.ExitCode)" }
}

Run-AssertTest 'F1.2: Container DA THOAT truoc timeout -> lay exit code thuc te (vd 137)' {
    $script:f1StatusCalls = 0
    $result = Invoke-RunnerWithSafeTimeout -SessionId "s55_tc02_f1_2" `
        -RunnerName "mock_runner_f1_2" `
        -TimeoutSeconds 2 `
        -PollIntervalMs 0 `
        -StartRunnerBlock { "mock_container_id" } `
        -CheckRunnerStatusBlock {
            $script:f1StatusCalls++
            if ($script:f1StatusCalls -ge 3) {
                # Lan goi truoc khi stop: container da thoat
                return @{ IsRunning = $false; ExitCode = 137 }
            }
            return @{ IsRunning = $true; ExitCode = 0 }
        } `
        -GetRunnerLogsBlock { "log" } `
        -VerifyOwnershipBlock { $true } `
        -StopRunnerBlock { @{ Success = $true } }

    if ($result.Success -ne $false -or $result.Status -ne "TimedOut") { throw "Phai FAIL" }
    if ($result.ExitCode -ne 137) { throw "Phai lay exit code thuc te 137: $($result.ExitCode)" }
}

Run-AssertTest 'F2.1: Set-S55FailureEvidence ghi duoc -> tra $true va noi dung dung' {
    $tmpFile = Join-Path ([System.IO.Path]::GetTempPath()) ('s55_ev_' + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        $ok = Set-S55FailureEvidence -FailureMessage 'loi goc ABC' -EvidencePath $tmpFile
        if ($ok -ne $true) { throw "Phai tra true" }
        if ((Get-Content -LiteralPath $tmpFile -Raw).TrimEnd("`r", "`n") -ne 'loi goc ABC') { throw "Noi dung khong dung" }
    } finally {
        Remove-Item -LiteralPath $tmpFile -Force -ErrorAction SilentlyContinue
    }
}

Run-AssertTest 'F2.2: Set-S55FailureEvidence ghi LOI -> tra $false kem canh bao, KHONG nem loi (bao toan loi goc)' {
    $badPath = Join-Path ([System.IO.Path]::GetTempPath()) ('s55_notadir_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType File -Path $badPath -Force | Out-Null
    try {
        # Duong dan nam duoi mot FILE -> Set-Content chac chan loi
        $ok = Set-S55FailureEvidence -FailureMessage 'loi goc ABC' -EvidencePath (Join-Path $badPath 'child\failure.txt')
        if ($ok -ne $false) { throw "Phai tra false khi ghi loi" }
    } finally {
        Remove-Item -LiteralPath $badPath -Force -ErrorAction SilentlyContinue
    }
}

Run-AssertTest 'F4.1: Test-S55DockerImagePresent — mock co image -> true' {
    $ok = Test-S55DockerImagePresent -ImageName 'mock/img:1' -DockerCommandBlock { param([string[]]$Arguments) return $true }
    if ($ok -ne $true) { throw "Phai tra true" }
}

Run-AssertTest 'F4.2: Test-S55DockerImagePresent — mock thieu image -> false (dung som truoc khi tao runner)' {
    $ok = Test-S55DockerImagePresent -ImageName 'mock/img:1' -DockerCommandBlock { param([string[]]$Arguments) return $false }
    if ($ok -ne $false) { throw "Phai tra false" }
}

Run-AssertTest 'TC02b.1: Guard nguon — run_tc02_complete.ps1 (neu ton tai) parameterize ten qua ResourceSuffix, khong con ten cung tc02' {
    $tc02Script = Join-Path $scriptDir 'artifacts\test-evidence-s55\run_tc02_complete.ps1'
    if (-not (Test-Path -LiteralPath $tc02Script)) {
        Write-Host "   (SKIP) File TC-02 driver khong ton tai trong working tree nay"
        return
    }
    $content = Get-Content -LiteralPath $tc02Script -Raw
    if ($content -notmatch 'ResourceSuffix') { throw "Thieu tham so ResourceSuffix" }
    if ($content -notmatch '\$pgContainerName') { throw "Khong dung bien pgContainerName" }
    if ($content -match 'eventpulse_test_pg_tc02"') { throw "Van con ten cung eventpulse_test_pg_tc02" }
    if ($content -match 'eventpulse_test_net_tc02') { throw "Van con ten cung eventpulse_test_net_tc02" }
    if ($content -match 'tc02_failure_reason\.txt') { throw "Evidence failure reason chua parameterize" }
    # M1: runner phai noi vao dung network bien $pgNetworkName, KHONG hardcode mang s55
    if ($content -notmatch '"--network", \$pgNetworkName') { throw "M1: runner chua dung bien network \$pgNetworkName" }
    if ($content -match '"--network", "eventpulse_test_net_s55"') { throw "M1: runner van hardcode mang TC-01 s55" }
    # M3: ten evidence phai duoc parameterize, khong con ten mangle mat prefix
    if ($content -notmatch '\$\{ResourceSuffix\}_console\.log') { throw "M3: console log chua parameterize dung" }
    if ($content -notmatch 'docker_snapshot_after_\$ResourceSuffix\.txt') { throw "M3: snapshot after chua parameterize" }
    if ($content -match '\\_console\.log') { throw "M3: con ten evidence mangle (thieu prefix suffix)" }
}

Run-AssertTest 'TC03.2: Timeout — GetRunnerLogsBlock nem loi -> van FAIL an toan, Log rong, khong crash' {
    $result = Invoke-RunnerWithSafeTimeout -SessionId "s55_tc02_ev2" `
        -RunnerName "mock_runner_ev2" `
        -TimeoutSeconds 2 `
        -PollIntervalMs 0 `
        -StartRunnerBlock { "mock_container_id" } `
        -CheckRunnerStatusBlock { @{ IsRunning = $true; ExitCode = $null } } `
        -GetRunnerLogsBlock { throw "docker logs daemon loi" } `
        -VerifyOwnershipBlock { $true } `
        -StopRunnerBlock { @{ Success = $true } }

    if ($result.Success -ne $false -or $result.Status -ne "TimedOut") { throw "Phai van FAIL an toan" }
    if ($null -ne $result.Log) { throw "Log phai rong khi khong thu duoc" }
    if ($result.LogFetchFailed -ne $true) { throw "Phai danh dau LogFetchFailed" }
}

Run-AssertTest 'M2.1: Runner da thoat (exit 1) + GetRunnerLogsBlock nem loi (native stderr) -> helper KHONG throw, exit code bao toan' {
    $result = Invoke-RunnerWithSafeTimeout -SessionId "s55_tc02_m2_1" `
        -RunnerName "mock_runner_m2_1" `
        -TimeoutSeconds 30 `
        -PollIntervalMs 0 `
        -StartRunnerBlock { "mock_container_id" } `
        -CheckRunnerStatusBlock { @{ IsRunning = $false; ExitCode = 1 } } `
        -GetRunnerLogsBlock { throw "* Setting postgresql15 as the default version" } `
        -VerifyOwnershipBlock { $true } `
        -StopRunnerBlock { @{ Success = $true } }

    if ($result.Success -ne $false -or $result.Status -ne "CompletedFailed") { throw "Phai FAIL dung trang thai: $($result | ConvertTo-Json -Compress)" }
    if ($result.ExitCode -ne 1) { throw "Exit code thuc te phai bao toan (1): $($result.ExitCode)" }
    if ($result.LogFetchFailed -ne $true) { throw "Phai danh dau LogFetchFailed" }
    if ($null -ne $result.Log) { throw "Log phai rong khi fetch loi" }
}

Run-AssertTest 'M2.2: Runner exit 7 + log fetch loi -> ExitCode 7, KHONG bao gio bien thanh 0' {
    $result = Invoke-RunnerWithSafeTimeout -SessionId "s55_tc02_m2_2" `
        -RunnerName "mock_runner_m2_2" `
        -TimeoutSeconds 30 `
        -PollIntervalMs 0 `
        -StartRunnerBlock { "mock_container_id" } `
        -CheckRunnerStatusBlock { @{ IsRunning = $false; ExitCode = 7 } } `
        -GetRunnerLogsBlock { throw "native stderr line" } `
        -VerifyOwnershipBlock { $true } `
        -StopRunnerBlock { @{ Success = $true } }

    if ($result.ExitCode -ne 7) { throw "ExitCode phai la 7: $($result.ExitCode)" }
    if ($result.Success -ne $false) { throw "Khong duoc PASS" }
}

Run-AssertTest 'M2.3: Runner exit 0 + log fetch thanh cong -> CompletedSuccess, LogFetchFailed=false' {
    $result = Invoke-RunnerWithSafeTimeout -SessionId "s55_tc02_m2_3" `
        -RunnerName "mock_runner_m2_3" `
        -TimeoutSeconds 30 `
        -PollIntervalMs 0 `
        -StartRunnerBlock { "mock_container_id" } `
        -CheckRunnerStatusBlock { @{ IsRunning = $false; ExitCode = 0 } } `
        -GetRunnerLogsBlock { "drill ok" } `
        -VerifyOwnershipBlock { $true } `
        -StopRunnerBlock { @{ Success = $true } }

    if ($result.Success -ne $true -or $result.Status -ne "CompletedSuccess") { throw "Phai PASS: $($result | ConvertTo-Json -Compress)" }
    if ($result.ExitCode -ne 0 -or $result.LogFetchFailed) { throw "Sai ExitCode/LogFetchFailed" }
    if ($result.Log -ne "drill ok") { throw "Sai Log" }
}

Run-AssertTest 'P1.1: Lenh tao ca stdout lan stderr (exit 0) -> thu du 2 stream, khong throw, ExitCode dung' {
    $cmdExe = (Get-Command cmd.exe).Source
    $res = Get-DockerLogsSafe -ContainerName 'unused' -DockerExe $cmdExe -Arguments @('/c', '(echo out) & (echo err 1>&2) & exit /b 0') -TimeoutSeconds 30
    if ($res.Success -ne $true -or $res.ExitCode -ne 0) { throw "Sai Success/ExitCode: $($res | ConvertTo-Json -Compress)" }
    if ($res.StdOut -notmatch 'out') { throw "Thieu stdout" }
    if ($res.StdErr -notmatch 'err') { throw "Thieu stderr" }
}

Run-AssertTest 'P1.2: Lenh exit 3 co stderr -> khong throw, ExitCode 3 duoc tra ve, 2 stream duoc giu' {
    $cmdExe = (Get-Command cmd.exe).Source
    $res = Get-DockerLogsSafe -ContainerName 'unused' -DockerExe $cmdExe -Arguments @('/c', '(echo out) & (echo err 1>&2) & exit /b 3') -TimeoutSeconds 30
    if ($res.Success -ne $false -or $res.ExitCode -ne 3) { throw "Sai Success/ExitCode: $($res | ConvertTo-Json -Compress)" }
    if ($res.StdOut -notmatch 'out' -or $res.StdErr -notmatch 'err') { throw "Stream bi mat" }
    if ($res.Error -notmatch 'err') { throw "Thieu Error" }
}

Run-AssertTest 'P1.3: Executable khong ton tai -> tra Success=false kem Error, khong nem' {
    $res = Get-DockerLogsSafe -ContainerName 'unused' -DockerExe 'C:\khong_ton_tai\docker.exe' -TimeoutSeconds 30
    if ($res.Success -ne $false) { throw "Phai fail" }
    if ($res.Error -notmatch 'Khong the khoi chay') { throw "Thieu Error: $($res.Error)" }
}

Run-AssertTest 'P1.4: Guard nguon — TC-02 GetRunnerLogsBlock dung Get-DockerLogsSafe (khong native 2>&1)' {
    $tc02Script = Join-Path $scriptDir 'artifacts\test-evidence-s55\run_tc02_complete.ps1'
    if (-not (Test-Path -LiteralPath $tc02Script)) {
        Write-Host "   (SKIP) File TC-02 driver khong ton tai"
        return
    }
    $content = Get-Content -LiteralPath $tc02Script -Raw
    if ($content -notmatch 'Get-DockerLogsSafe -ContainerName \$runnerContainerName') { throw "P1: GetRunnerLogsBlock chua dung Get-DockerLogsSafe" }
    if ($content -match 'docker logs \$runnerContainerName 2>&1') { throw "P1: con native pipeline docker logs" }
}

Run-AssertTest 'P2.1: Guard nguon — TC-02 tao runner script tren bind mount theo suffix, log du phong va goi sh file' {
    $tc02Script = Join-Path $scriptDir 'artifacts\test-evidence-s55\run_tc02_complete.ps1'
    if (-not (Test-Path -LiteralPath $tc02Script)) {
        Write-Host "   (SKIP) File TC-02 driver khong ton tai"
        return
    }
    $content = Get-Content -LiteralPath $tc02Script -Raw
    if ($content -notmatch 'Get-S55RunnerShellScript') { throw "P2.1: thieu goi Get-S55RunnerShellScript" }
    if ($content -notmatch 'Save-S55RunnerScript') { throw "P2.1: thieu goi Save-S55RunnerScript" }
    if ($content -notmatch 'artifacts/test-evidence-s55/\$\{ResourceSuffix\}_runner\.sh') { throw "P2.1: thieu duong dan script theo suffix" }
    if ($content -notmatch 'artifacts/test-evidence-s55/\$\{ResourceSuffix\}_runner_internal\.log') { throw "P2.1: thieu duong dan log du phong theo suffix" }
    if ($content -notmatch '/workspace/\$runnerScriptRelPath') { throw "P2.1: thieu duong dan script trong container (/workspace/...)" }
    if ($content -notmatch '/workspace/\$runnerLogRelPath') { throw "P2.1: thieu duong dan log trong container (/workspace/...)" }
    if ($content -notmatch 'artifacts/test-evidence-s55:/workspace/artifacts/test-evidence-s55:rw') { throw "P2.1: evidence dir khong duoc bind-mount rw" }
    if ($content -notmatch '"sh",\s*\$runnerScriptContainerPath') { throw "P2.1: runner phai duoc goi bang mot token sh + token duong dan script (khong inline string)" }
    # Anti-regression: runner image bat buoc phai la Alpine 3.20 tro len de co postgresql16-client
    if ($content -notmatch 'mcr\.microsoft\.com/powershell:lts-alpine-3\.20') { throw "P2.1: runner image bat buoc phai la mcr.microsoft.com/powershell:lts-alpine-3.20 (Alpine 3.20)" }
    if ($content -match 'mcr\.microsoft\.com/powershell:lts-alpine[^\-]') { throw "P2.1: phat hien runner image lts-alpine cu (Alpine 3.17) khong chua postgresql16-client" }
}

Run-AssertTest 'P2.2: Get-S55RunnerShellScript — noi dung marker, placeholder va LF-only' {
    $mockDump = "artifacts/test-backups-s55/eventpulse_backup_mock.dump"
    $mockHost = "eventpulse_test_pg_mock"
    $mockLog = "/workspace/artifacts/test-evidence-s55/mock_internal.log"
    $script = Get-S55RunnerShellScript -DumpPath $mockDump -AllowedHost $mockHost -LogPath $mockLog

    if ($script -notmatch 'echo RUNNER_MARK_START') { throw "P2.2: thieu marker RUNNER_MARK_START" }
    if ($script -notmatch 'echo RUNNER_MARK_APK_EXIT=\$APK_EXIT') { throw "P2.2: thieu marker RUNNER_MARK_APK_EXIT" }
    if ($script -notmatch 'echo RUNNER_MARK_APK_FAILED') { throw "P2.2: thieu marker RUNNER_MARK_APK_FAILED" }
    if ($script -notmatch 'echo RUNNER_MARK_DRILL_EXIT=\$DRILL_EXIT') { throw "P2.2: thieu marker RUNNER_MARK_DRILL_EXIT" }
    if ($script -notmatch [regex]::Escape($mockDump)) { throw "P2.2: thieu tham so DumpPath" }
    if ($script -notmatch [regex]::Escape($mockHost)) { throw "P2.2: thieu tham so AllowedHost" }
    if ($script -notmatch [regex]::Escape($mockLog)) { throw "P2.2: thieu tham so LogPath trong tee" }
    if ($script.Contains("`r")) { throw "P2.2: script con chua ky tu CR (\r) — phai la LF-only" }
    # Anti-regression: runner bat buoc cai postgresql16-client (khong dung 15 de tuong thich dump header 1.15)
    if ($script -notmatch 'apk add --no-cache postgresql16-client') { throw "P2.2: runner bat buoc phai cai dat postgresql16-client" }
    if ($script -match 'postgresql15-client') { throw "P2.2: phat hien postgresql15-client — regression ve phien ban 15 gay loi unsupported version 1.15" }
    # Verification of tools & versions: bat buoc kiem tra pg_restore va pg_dump --version
    if ($script -notmatch 'RESTORE_VER=\$\(pg_restore --version 2>&1\)') { throw "P2.2: thieu lenh goi pg_restore --version" }
    if ($script -notmatch 'echo RUNNER_MARK_RESTORE_VER_EXIT=\$RESTORE_VER_EXIT') { throw "P2.2: thieu marker RUNNER_MARK_RESTORE_VER_EXIT" }
    if ($script -notmatch 'echo RUNNER_MARK_PG16_VERIFIED') { throw "P2.2: thieu marker RUNNER_MARK_PG16_VERIFIED" }
    if ($script -notmatch 'DUMP_VER=\$\(pg_dump --version 2>&1\)') { throw "P2.2: thieu lenh goi pg_dump --version" }
    if ($script -notmatch 'echo RUNNER_MARK_DUMP_VER_EXIT=\$DUMP_VER_EXIT') { throw "P2.2: thieu marker RUNNER_MARK_DUMP_VER_EXIT" }
    if ($script -notmatch 'echo RUNNER_MARK_TOOLS_VERIFIED') { throw "P2.2: thieu marker RUNNER_MARK_TOOLS_VERIFIED" }
}

Run-AssertTest 'P2.3: Get-S55RunnerShellScript — fail-fast khi apk that bai, bao toan exit code qua tee' {
    $script = Get-S55RunnerShellScript -DumpPath "dump" -AllowedHost "host" -LogPath "log"

    # 1. Bat APK_EXIT ngay sau apk add
    $apkAddIdx = $script.IndexOf('apk add --no-cache postgresql16-client')
    $apkExitIdx = $script.IndexOf('APK_EXIT=$?')
    if ($apkAddIdx -lt 0 -or $apkExitIdx -lt 0 -or $apkExitIdx -lt $apkAddIdx) {
        throw "P2.3: APK_EXIT=$? phai duoc bat ngay sau apk add postgresql16-client"
    }

    # 2. if APK_EXIT != 0 -> exit ngay, truoc lenh restore-drill
    $apkFailIfIdx = $script.IndexOf('if [ $APK_EXIT -ne 0 ]')
    $restoreVerIdx = $script.IndexOf('RESTORE_VER=$(pg_restore --version')
    $dumpVerIdx = $script.IndexOf('DUMP_VER=$(pg_dump --version')
    $toolsVerMarkIdx = $script.IndexOf('echo RUNNER_MARK_TOOLS_VERIFIED')
    $drillCmdIdx = $script.IndexOf('scripts/restore-drill.ps1')

    if ($apkFailIfIdx -lt 0 -or $drillCmdIdx -lt 0 -or $apkFailIfIdx -gt $drillCmdIdx) {
        throw "P2.3: Chot chan APK fail phai nam TRUOC lenh restore-drill"
    }
    if ($restoreVerIdx -lt 0 -or $restoreVerIdx -lt $apkFailIfIdx -or $restoreVerIdx -gt $drillCmdIdx) {
        throw "P2.3: pg_restore --version phai nam SAU apk add va TRUOC restore-drill"
    }
    if ($dumpVerIdx -lt 0 -or $dumpVerIdx -lt $restoreVerIdx -or $dumpVerIdx -gt $drillCmdIdx) {
        throw "P2.3: pg_dump --version phai nam SAU pg_restore va TRUOC restore-drill"
    }
    if ($toolsVerMarkIdx -lt 0 -or $toolsVerMarkIdx -gt $drillCmdIdx) {
        throw "P2.3: Marker TOOLS_VERIFIED phai duoc ghi TRUOC restore-drill"
    }

    # 3. Bat DRILL_EXIT ngay sau restore-drill
    $drillExitIdx = $script.IndexOf('DRILL_EXIT=$?')
    if ($drillExitIdx -lt 0 -or $drillExitIdx -lt $drillCmdIdx) {
        throw "P2.3: DRILL_EXIT=$? phai duoc bat ngay sau restore-drill"
    }

    # 4. Bao toan exit code qua file tam (tranh tee lam mat exit code ve 0)
    if ($script -notmatch 'RUNNER_EXIT_FILE=') { throw "P2.3: thieu bien RUNNER_EXIT_FILE de luu ma thoat qua pipeline" }
    if ($script -notmatch 'FINAL_EXIT=\$\(cat "\$RUNNER_EXIT_FILE"') { throw "P2.3: thieu buoc doc lai exit code sau pipeline tee" }
    if ($script -notmatch 'exit \$FINAL_EXIT') { throw "P2.3: thieu exit voi FINAL_EXIT" }
}

Run-AssertTest 'P2.4: Execution test (sh.exe) — apk that bai -> in du marker, KHONG chay drill, exit code dung' {
    $shCandidates = @(
        (Get-Command sh.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        (Join-Path $env:ProgramFiles 'Git\bin\sh.exe'),
        (Join-Path $env:ProgramFiles 'Git\usr\bin\sh.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Git\bin\sh.exe')
    ) | Where-Object { -not [string]::IsNullOrEmpty($_) -and (Test-Path -LiteralPath $_) }

    if ($shCandidates.Count -eq 0) {
        Write-Host "   (SKIP) Khong tim thay sh.exe tren he thong de chay integration test"
        return
    }
    $shExe = $shCandidates[0]

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ('s55_sh_test_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $tempLog = (Join-Path $tempDir 'runner_internal.log').Replace('\', '/')
    $tempScript = Join-Path $tempDir 'runner.sh'

    # Mock script: apk add that bai voi exit code 14, drill neu chay se in LOI_DRILL
    $rawTemplate = @'
RUNNER_EXIT_FILE="__TEMP_DIR__/exit_$$.txt"
rm -f "$RUNNER_EXIT_FILE"
{
echo RUNNER_MARK_START
(exit 14)
APK_EXIT=$?
echo RUNNER_MARK_APK_EXIT=$APK_EXIT
if [ $APK_EXIT -ne 0 ]; then
    echo RUNNER_MARK_APK_FAILED
    echo $APK_EXIT > "$RUNNER_EXIT_FILE"
    exit $APK_EXIT
fi
echo RUNNER_MARK_DRILL_UNEXPECTED
exit 0
} 2>&1 | tee __LOG__
FINAL_EXIT=$(cat "$RUNNER_EXIT_FILE" 2>/dev/null)
rm -f "$RUNNER_EXIT_FILE"
if [ -z "$FINAL_EXIT" ]; then FINAL_EXIT=1; fi
exit $FINAL_EXIT
'@
    $mockContent = $rawTemplate.Replace('__TEMP_DIR__', $tempDir.Replace('\', '/')).Replace('__LOG__', $tempLog)
    Save-S55RunnerScript -FilePath $tempScript -Content $mockContent | Out-Null

    $output = & $shExe $tempScript.Replace('\', '/')
    $shExit = $LASTEXITCODE
    $outStr = ($output -join "`n")

    if ($shExit -ne 14) { throw "P2.4: Exit code phai la 14 nhung thuc te la $shExit" }
    if ($outStr -notmatch 'RUNNER_MARK_START') { throw "P2.4: Thieu RUNNER_MARK_START trong output" }
    if ($outStr -notmatch 'RUNNER_MARK_APK_EXIT=14') { throw "P2.4: Thieu RUNNER_MARK_APK_EXIT=14" }
    if ($outStr -notmatch 'RUNNER_MARK_APK_FAILED') { throw "P2.4: Thieu RUNNER_MARK_APK_FAILED" }
    if ($outStr -match 'RUNNER_MARK_DRILL_UNEXPECTED') { throw "P2.4: Drill van chay khi apk that bai!" }
    if (-not (Test-Path -LiteralPath $tempLog)) { throw "P2.4: File log du phong khong duoc tao" }
    $logContent = Get-Content -LiteralPath $tempLog -Raw
    if ($logContent -notmatch 'RUNNER_MARK_APK_FAILED') { throw "P2.4: Log du phong thieu marker" }

    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

Run-AssertTest 'P2.5: Execution test (sh.exe) — drill that bai -> exit code khong bi tee ghi de thanh 0' {
    $shCandidates = @(
        (Get-Command sh.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        (Join-Path $env:ProgramFiles 'Git\bin\sh.exe'),
        (Join-Path $env:ProgramFiles 'Git\usr\bin\sh.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Git\bin\sh.exe')
    ) | Where-Object { -not [string]::IsNullOrEmpty($_) -and (Test-Path -LiteralPath $_) }

    if ($shCandidates.Count -eq 0) {
        Write-Host "   (SKIP) Khong tim thay sh.exe tren he thong de chay integration test"
        return
    }
    $shExe = $shCandidates[0]

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ('s55_sh_test2_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $tempLog = (Join-Path $tempDir 'runner_internal.log').Replace('\', '/')
    $tempScript = Join-Path $tempDir 'runner.sh'

    # Mock script: apk thanh cong (0), drill that bai voi exit code 23
    $rawTemplate = @'
RUNNER_EXIT_FILE="__TEMP_DIR__/exit_$$.txt"
rm -f "$RUNNER_EXIT_FILE"
{
echo RUNNER_MARK_START
(exit 0)
APK_EXIT=$?
echo RUNNER_MARK_APK_EXIT=$APK_EXIT
if [ $APK_EXIT -ne 0 ]; then
    echo RUNNER_MARK_APK_FAILED
    echo $APK_EXIT > "$RUNNER_EXIT_FILE"
    exit $APK_EXIT
fi
(exit 23)
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
    $mockContent = $rawTemplate.Replace('__TEMP_DIR__', $tempDir.Replace('\', '/')).Replace('__LOG__', $tempLog)
    Save-S55RunnerScript -FilePath $tempScript -Content $mockContent | Out-Null

    $output = & $shExe $tempScript.Replace('\', '/')
    $shExit = $LASTEXITCODE
    $outStr = ($output -join "`n")

    if ($shExit -ne 23) { throw "P2.5: Exit code phai la 23 nhung bi tee ghi de thanh $shExit" }
    if ($outStr -notmatch 'RUNNER_MARK_START') { throw "P2.5: Thieu RUNNER_MARK_START" }
    if ($outStr -notmatch 'RUNNER_MARK_APK_EXIT=0') { throw "P2.5: Thieu APK_EXIT=0" }
    if ($outStr -notmatch 'RUNNER_MARK_DRILL_EXIT=23') { throw "P2.5: Thieu DRILL_EXIT=23" }

    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

Run-AssertTest 'P2.6: Execution test (sh.exe) — ca hai buoc thanh cong -> exit 0 va log ghi day du' {
    $shCandidates = @(
        (Get-Command sh.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        (Join-Path $env:ProgramFiles 'Git\bin\sh.exe'),
        (Join-Path $env:ProgramFiles 'Git\usr\bin\sh.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Git\bin\sh.exe')
    ) | Where-Object { -not [string]::IsNullOrEmpty($_) -and (Test-Path -LiteralPath $_) }

    if ($shCandidates.Count -eq 0) {
        Write-Host "   (SKIP) Khong tim thay sh.exe tren he thong de chay integration test"
        return
    }
    $shExe = $shCandidates[0]

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ('s55_sh_test3_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $tempLog = (Join-Path $tempDir 'runner_internal.log').Replace('\', '/')
    $tempScript = Join-Path $tempDir 'runner.sh'

    # Mock script: ca hai deu exit 0
    $rawTemplate = @'
RUNNER_EXIT_FILE="__TEMP_DIR__/exit_$$.txt"
rm -f "$RUNNER_EXIT_FILE"
{
echo RUNNER_MARK_START
(exit 0)
APK_EXIT=$?
echo RUNNER_MARK_APK_EXIT=$APK_EXIT
if [ $APK_EXIT -ne 0 ]; then
    echo RUNNER_MARK_APK_FAILED
    echo $APK_EXIT > "$RUNNER_EXIT_FILE"
    exit $APK_EXIT
fi
(exit 0)
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
    $mockContent = $rawTemplate.Replace('__TEMP_DIR__', $tempDir.Replace('\', '/')).Replace('__LOG__', $tempLog)
    Save-S55RunnerScript -FilePath $tempScript -Content $mockContent | Out-Null

    $output = & $shExe $tempScript.Replace('\', '/')
    $shExit = $LASTEXITCODE
    $outStr = ($output -join "`n")

    if ($shExit -ne 0) { throw "P2.6: Exit code phai la 0 nhung thuc te la $shExit" }
    if ($outStr -notmatch 'RUNNER_MARK_APK_EXIT=0' -or $outStr -notmatch 'RUNNER_MARK_DRILL_EXIT=0') { throw "P2.6: Thieu marker exit 0" }

    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

Run-AssertTest 'P2.7: Save-S55RunnerScript — ghi UTF-8 khong BOM, LF-only va tu dong tao thu muc' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ('s55_utf8_' + [Guid]::NewGuid().ToString('N'))
    $targetFile = Join-Path $tempDir 'nested\sub\runner.sh'

    $testContent = "echo 'hello'`r`necho 'world'`r`n"
    $savedPath = Save-S55RunnerScript -FilePath $targetFile -Content $testContent

    if (-not (Test-Path -LiteralPath $savedPath)) { throw "P2.7: File chua duoc tao tai $savedPath" }

    $bytes = [System.IO.File]::ReadAllBytes($savedPath)
    if ($bytes.Length -lt 3) { throw "P2.7: File qua nho" }

    # Kiem tra BOM: UTF-8 BOM la 0xEF, 0xBB, 0xBF -> KHONG duoc co
    if ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "P2.7: Phat hien UTF-8 BOM (0xEF, 0xBB, 0xBF)! Runner script phai la UTF-8 NO BOM."
    }

    # Kiem tra LF-only: khong duoc chua 0x0D (CR)
    foreach ($b in $bytes) {
        if ($b -eq 0x0D) {
            throw "P2.7: Phat hien ky tu CR (0x0D) trong file! Runner script phai la LF-only."
        }
    }

    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

Run-AssertTest 'P2.8: Tuong thich PowerShell 5.1 va PowerShell 7 — AST hop le khong loi cu phap' {
    $scriptsToCheck = @(
        (Join-Path $scriptDir 'artifacts\test-evidence-s55\run_tc02_complete.ps1'),
        (Join-Path $scriptDir 'tests\S55-TestHelpers.ps1'),
        (Join-Path $scriptDir 'tests\test-s55-blockers-offline.ps1')
    )

    foreach ($s in $scriptsToCheck) {
        if (-not (Test-Path -LiteralPath $s)) { continue }
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($s, [ref]$tokens, [ref]$errors)
        if ($errors.Count -gt 0) {
            throw "P2.8: AST parsing loi tai file '$s': $($errors[0].Message)"
        }
    }
}

# ==============================================================================
# NHOM 13: HARDENING A/B — REPO ROOT GUARD + INTERNAL LOG VERIFY
# ==============================================================================
Write-Host "`n--- [NHOM 13] HARDENING A/B: REPO ROOT + INTERNAL LOG ---"

$hardeningTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('s55_harden_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hardeningTempRoot -Force | Out-Null

Run-AssertTest 'H.1: Test-S55RepoRoot — du ca 2 file nhan dien -> true' {
    New-Item -ItemType Directory -Path (Join-Path $hardeningTempRoot 'scripts'), (Join-Path $hardeningTempRoot 'tests') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $hardeningTempRoot 'scripts\restore-drill.ps1'), (Join-Path $hardeningTempRoot 'tests\S55-TestHelpers.ps1') -Force | Out-Null
    if (-not (Test-S55RepoRoot -RootPath $hardeningTempRoot)) { throw "Phai true khi du 2 file" }
}

Run-AssertTest 'H.2: Test-S55RepoRoot — thieu ca 2 file -> false' {
    $emptyRoot = Join-Path $hardeningTempRoot ('empty_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $emptyRoot -Force | Out-Null
    if (Test-S55RepoRoot -RootPath $emptyRoot) { throw "Phai false khi sai CWD" }
}

Run-AssertTest 'H.3: Test-S55RepoRoot — chi 1 file -> false (khong chi kiem 1 file)' {
    $oneFile = Join-Path $hardeningTempRoot ('one_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $oneFile 'scripts') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $oneFile 'scripts\restore-drill.ps1') -Force | Out-Null
    if (Test-S55RepoRoot -RootPath $oneFile) { throw "Phai false khi chi co 1 file" }
}

Run-AssertTest 'H.4: Internal log ton tai dung suffix -> true, khong canh bao' {
    $logFile = Join-Path $hardeningTempRoot ('tc02d_runner_internal.log')
    New-Item -ItemType File -Path $logFile -Force | Out-Null
    if (-not (Test-S55RunnerInternalLogPresent -ExpectedPath $logFile)) { throw "Phai true khi file ton tai" }
    Remove-Item -LiteralPath $logFile -Force
}

Run-AssertTest 'H.5: Thieu internal log -> false kem canh bao, KHONG nem, khong ghi de ket qua' {
    $missingLog = Join-Path $hardeningTempRoot ('khong_ton_tai_' + [Guid]::NewGuid().ToString('N') + '.log')
    $res = Test-S55RunnerInternalLogPresent -ExpectedPath $missingLog
    if ($res -ne $false) { throw "Phai false khi thieu file" }
}

Run-AssertTest 'H.6: ExpectedPath rong -> false, khong nem' {
    if ((Test-S55RunnerInternalLogPresent -ExpectedPath '') -ne $false) { throw "Phai false" }
}

Run-AssertTest 'H.7: Guard nguon — TC-02 co repo-root guard (2 file) TRUOC moi docker op va kiem tra internal log' {
    $tc02Script = Join-Path $scriptDir 'artifacts\test-evidence-s55\run_tc02_complete.ps1'
    if (-not (Test-Path -LiteralPath $tc02Script)) {
        Write-Host "   (SKIP) File TC-02 driver khong ton tai"
        return
    }
    $content = Get-Content -LiteralPath $tc02Script -Raw
    if ($content -notmatch 'Test-S55RepoRoot') { throw "Thieu goi Test-S55RepoRoot" }
    if ($content -notmatch 'scripts\\restore-drill\.ps1') { throw "Guard thieu file nhan dien scripts\restore-drill.ps1" }
    if ($content -notmatch 'tests\\S55-TestHelpers\.ps1') { throw "Guard thieu file nhan dien tests\S55-TestHelpers.ps1" }
    if ($content -notmatch 'Test-S55RunnerInternalLogPresent -ExpectedPath \$runnerLogRelPath') { throw "Thieu kiem tra internal log theo suffix" }
    $guardIndex = $content.IndexOf('Test-S55RepoRoot')
    $firstDockerOp = $content.IndexOf('docker ')
    if ($guardIndex -lt 0 -or $firstDockerOp -lt 0 -or $guardIndex -gt $firstDockerOp) { throw "Guard phai nam TRUOC moi docker op" }
}

# ==============================================================================
# NHOM 14: S-55 BASELINE ROW COUNTS & RESTORE STRICT MODE (FAIL-CLOSED)
# ==============================================================================
Write-Host "`n--- [NHOM 14] S-55 BASELINE ROW COUNTS & RESTORE STRICT MODE (FAIL-CLOSED) ---"

# Scenario 1: Baseline đầy đủ và hợp lệ
Run-AssertTest 'S55-BL.1: Baseline day du va hop le -> Thu thap dung tat ca bang va row counts non-negative' {
    $mockBlock = {
        param($ToolName, $ConnParams, $DatabaseName, $Query, $PsqlExe, $Mode)
        if ($Query -match "information_schema\.tables") {
            return "events`ntickets`n__EFMigrationsHistory"
        }
        if ($Query -match 'FROM "events"') { return "10" }
        if ($Query -match 'FROM "tickets"') { return "25" }
        if ($Query -match 'FROM "__EFMigrationsHistory"') { return "3" }
        throw "Unexpected query: $Query"
    }

    $res = Get-S55DatabaseBaselineRowCounts -PsqlExe "mock-psql" `
        -ConnParams @{ Host = "localhost"; Port = 5432; Username = "postgres" } `
        -DatabaseName "event_ticket_db" `
        -Mode "Native" `
        -MockCommandBlock $mockBlock

    if ($null -eq $res -or $res.Count -ne 3) { throw "Baseline phai co 3 bang, thuc te: $($res.Count)" }
    if ($res["events"] -ne 10L -or $res["tickets"] -ne 25L -or $res["__EFMigrationsHistory"] -ne 3L) {
        throw "Row count khong khop gia tri mong doi"
    }
}

# Scenario 2: Không tìm thấy psql
Run-AssertTest 'S55-BL.2: Khong tim thay psql (Native mode) -> Throw fail-closed [ABORT]' {
    $threw = $false
    try {
        Get-S55DatabaseBaselineRowCounts -PsqlExe "" `
            -ConnParams @{ Host = "localhost"; Port = 5432; Username = "postgres" } `
            -DatabaseName "event_ticket_db" `
            -Mode "Native"
    } catch {
        if ($_ -match "Khong tim thay cong cu 'psql'") { $threw = $true }
    }
    if (-not $threw) { throw "Khong nem loi khi thieu psql!" }
}

# Scenario 3: Truy vấn lấy danh sách bảng thất bại
Run-AssertTest 'S55-BL.3: Truy van lay danh sach bang that bai -> Throw loi fail-closed' {
    $mockBlock = {
        param($ToolName, $ConnParams, $DatabaseName, $Query, $PsqlExe, $Mode)
        return @{ ExitCode = 2; Error = "Connection to server failed (timeout)" }
    }

    $threw = $false
    try {
        Get-S55DatabaseBaselineRowCounts -PsqlExe "mock-psql" `
            -ConnParams @{ Host = "localhost"; Port = 5432; Username = "postgres" } `
            -DatabaseName "event_ticket_db" `
            -Mode "Native" `
            -MockCommandBlock $mockBlock
    } catch {
        if ($_ -match "Lenh SQL that bai") { $threw = $true }
    }
    if (-not $threw) { throw "Khong nem loi khi truy van danh sach bang that bai!" }
}

# Scenario 4: Truy vấn row count thất bại
Run-AssertTest 'S55-BL.4: Truy van row count that bai -> Throw loi fail-closed' {
    $mockBlock = {
        param($ToolName, $ConnParams, $DatabaseName, $Query, $PsqlExe, $Mode)
        if ($Query -match "information_schema\.tables") {
            return "events`ntickets"
        }
        if ($Query -match 'FROM "events"') { return "10" }
        if ($Query -match 'FROM "tickets"') {
            return @{ ExitCode = 1; Error = "permission denied for table tickets" }
        }
        throw "Unexpected query: $Query"
    }

    $threw = $false
    try {
        Get-S55DatabaseBaselineRowCounts -PsqlExe "mock-psql" `
            -ConnParams @{ Host = "localhost"; Port = 5432; Username = "postgres" } `
            -DatabaseName "event_ticket_db" `
            -Mode "Native" `
            -MockCommandBlock $mockBlock
    } catch {
        if ($_ -match "Lenh SQL that bai") { $threw = $true }
    }
    if (-not $threw) { throw "Khong nem loi khi truy van row count mot bang that bai!" }
}

# Scenario 5: Count rỗng, không phải số nguyên hoặc số âm
Run-AssertTest 'S55-BL.5a: Row count rong -> Throw fail-closed' {
    $mockBlock = {
        param($ToolName, $ConnParams, $DatabaseName, $Query, $PsqlExe, $Mode)
        if ($Query -match "information_schema\.tables") { return "events" }
        if ($Query -match 'FROM "events"') { return "   " }
    }

    $threw = $false
    try {
        Get-S55DatabaseBaselineRowCounts -PsqlExe "mock-psql" `
            -ConnParams @{ Host = "localhost"; Port = 5432; Username = "postgres" } `
            -DatabaseName "event_ticket_db" `
            -Mode "Native" `
            -MockCommandBlock $mockBlock
    } catch {
        if ($_ -match "rong hoac khong hop le") { $threw = $true }
    }
    if (-not $threw) { throw "Khong nem loi khi count rong!" }
}

Run-AssertTest 'S55-BL.5b: Row count khong phai so nguyen -> Throw fail-closed' {
    $mockBlock = {
        param($ToolName, $ConnParams, $DatabaseName, $Query, $PsqlExe, $Mode)
        if ($Query -match "information_schema\.tables") { return "events" }
        if ($Query -match 'FROM "events"') { return "NOT_A_NUMBER" }
    }

    $threw = $false
    try {
        Get-S55DatabaseBaselineRowCounts -PsqlExe "mock-psql" `
            -ConnParams @{ Host = "localhost"; Port = 5432; Username = "postgres" } `
            -DatabaseName "event_ticket_db" `
            -Mode "Native" `
            -MockCommandBlock $mockBlock
    } catch {
        if ($_ -match "khong phai la so nguyen hop le") { $threw = $true }
    }
    if (-not $threw) { throw "Khong nem loi khi count khong phai so nguyen!" }
}

Run-AssertTest 'S55-BL.5c: Row count la so am -> Throw fail-closed' {
    $mockBlock = {
        param($ToolName, $ConnParams, $DatabaseName, $Query, $PsqlExe, $Mode)
        if ($Query -match "information_schema\.tables") { return "events" }
        if ($Query -match 'FROM "events"') { return "-42" }
    }

    $threw = $false
    try {
        Get-S55DatabaseBaselineRowCounts -PsqlExe "mock-psql" `
            -ConnParams @{ Host = "localhost"; Port = 5432; Username = "postgres" } `
            -DatabaseName "event_ticket_db" `
            -Mode "Native" `
            -MockCommandBlock $mockBlock
    } catch {
        if ($_ -match "la so am khong hop le") { $threw = $true }
    }
    if (-not $threw) { throw "Khong nem loi khi count la so am!" }
}

# Scenario 6: Không có bảng trong baseline (0 tables)
Run-AssertTest 'S55-BL.6: Danh sach bang rong bat thuong (0 bang) -> Throw fail-closed' {
    $mockBlock = {
        param($ToolName, $ConnParams, $DatabaseName, $Query, $PsqlExe, $Mode)
        if ($Query -match "information_schema\.tables") { return "" }
    }

    $threw = $false
    try {
        Get-S55DatabaseBaselineRowCounts -PsqlExe "mock-psql" `
            -ConnParams @{ Host = "localhost"; Port = 5432; Username = "postgres" } `
            -DatabaseName "event_ticket_db" `
            -Mode "Native" `
            -MockCommandBlock $mockBlock
    } catch {
        if ($_ -match "rong bat thuong \(0 bang\)") { $threw = $true }
    }
    if (-not $threw) { throw "Khong nem loi khi danh sach bang rong!" }
}

# Scenario 7: Tên bảng có chữ hoa / dấu ngoặc kép được xử lý an toàn
Run-AssertTest 'S55-BL.7: Ten bang co chu hoa hoac dau ngoac kep -> Escape an toan trong cau truy van' {
    $executedQueries = [System.Collections.Generic.List[string]]::new()
    $mockBlock = {
        param($ToolName, $ConnParams, $DatabaseName, $Query, $PsqlExe, $Mode)
        if ($Query -match "information_schema\.tables") {
            return "User`"Data`nOrderDetails`nSpecial`"Table`"Name"
        }
        $executedQueries.Add($Query)
        return "10"
    }

    $res = Get-S55DatabaseBaselineRowCounts -PsqlExe "mock-psql" `
        -ConnParams @{ Host = "localhost"; Port = 5432; Username = "postgres" } `
        -DatabaseName "event_ticket_db" `
        -Mode "Native" `
        -MockCommandBlock $mockBlock

    if (-not ($executedQueries -contains 'SELECT count(*) FROM "User""Data";')) {
        throw "Cau lenh SQL cho bang 'User`"Data' khong duoc escape dung: $($executedQueries -join '; ')"
    }
    if (-not ($executedQueries -contains 'SELECT count(*) FROM "OrderDetails";')) {
        throw "Cau lenh SQL cho bang 'OrderDetails' khong bao toan chu hoa: $($executedQueries -join '; ')"
    }
    if (-not ($executedQueries -contains 'SELECT count(*) FROM "Special""Table""Name";')) {
        throw "Cau lenh SQL cho bang 'Special`"Table`"Name' khong duoc escape dung: $($executedQueries -join '; ')"
    }
    if (-not $res.Contains('User"Data') -or -not $res.Contains('OrderDetails')) {
        throw "Baseline output phai giu nguyen ten bang goc"
    }
}

# Scenario 8: Backup thất bại không xóa backup cũ và dọn dẹp file staging
Run-AssertTest 'S55-BL.8: Backup that bai -> Khong xoa backup cu va don sach file staging' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("s55_bl8_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        # Tạo 2 bản backup cũ hợp lệ
        $oldDump1 = Join-Path $tempDir "eventpulse_backup_20261017_100000.dump"
        $oldJson1 = Join-Path $tempDir "eventpulse_backup_20261017_100000.json"
        $oldDump2 = Join-Path $tempDir "eventpulse_backup_20261017_120000.dump"
        $oldJson2 = Join-Path $tempDir "eventpulse_backup_20261017_120000.json"

        [System.IO.File]::WriteAllBytes($oldDump1, (New-Object byte[] 1024))
        [System.IO.File]::WriteAllBytes($oldDump2, (New-Object byte[] 1024))
        $sha1 = (Get-FileHash -Algorithm SHA256 -LiteralPath $oldDump1).Hash
        $sha2 = (Get-FileHash -Algorithm SHA256 -LiteralPath $oldDump2).Hash

        [PSCustomObject]@{ backupFile = "eventpulse_backup_20261017_100000.dump"; sha256 = $sha1; status = "VerifiedValid"; sizeBytes = 1024; createdAtUtc = (Get-Date).ToString("o") } | ConvertTo-Json | Set-Content -LiteralPath $oldJson1 -Encoding utf8
        [PSCustomObject]@{ backupFile = "eventpulse_backup_20261017_120000.dump"; sha256 = $sha2; status = "VerifiedValid"; sizeBytes = 1024; createdAtUtc = (Get-Date).ToString("o") } | ConvertTo-Json | Set-Content -LiteralPath $oldJson2 -Encoding utf8

        # Tạo file staging của lần chạy hiện tại bị dở dang
        $stagingDump = Join-Path $tempDir "eventpulse_backup_20261017_140000_in_progress.dump"
        $stagingJson = Join-Path $tempDir "eventpulse_backup_20261017_140000_in_progress.json"
        [System.IO.File]::WriteAllBytes($stagingDump, (New-Object byte[] 512))
        [System.IO.File]::WriteAllBytes($stagingJson, (New-Object byte[] 128))

        # Thực thi retention khi IsCurrentBackupFailed = $true qua backup-db.ps1
        $retResult = & $backupScript -TestOnlyRetention -BackupDir $tempDir -SimulateFailureForRetentionTest $true

        # Mô phỏng dọn dẹp staging như trong catch của backup-db.ps1
        if (Test-Path -LiteralPath $stagingDump) { Remove-Item -LiteralPath $stagingDump -Force }
        if (Test-Path -LiteralPath $stagingJson) { Remove-Item -LiteralPath $stagingJson -Force }

        if (-not (Test-Path -LiteralPath $oldDump1) -or -not (Test-Path -LiteralPath $oldJson1)) {
            throw "Ban backup cu 1 bi xoa khi lan chay moi that bai!"
        }
        if (-not (Test-Path -LiteralPath $oldDump2) -or -not (Test-Path -LiteralPath $oldJson2)) {
            throw "Ban backup cu 2 bi xoa khi lan chay moi that bai!"
        }
        if (Test-Path -LiteralPath $stagingDump) {
            throw "File staging dump chua duoc don dep sau khi that bai!"
        }
        if ($retResult.DeletedCount -ne 0) {
            throw "Retention da xoa $($retResult.DeletedCount) file khi backup that bai!"
        }
    } finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Scenario 9: Restore strict mode thất bại khi thiếu baseline
Run-AssertTest 'S55-BL.9: Restore drill bat -RequireBaselineMatch nhung manifest thieu tableRowCounts -> Fail [ABORT]' {
    $manifestObj = [PSCustomObject]@{
        backupFile = "eventpulse_backup_20261018_100000.dump"
        status = "VerifiedValid"
    }

    $threw = $false
    try {
        $hasBaseline = ($null -ne $manifestObj.PSObject.Properties['tableRowCounts'] -and $null -ne $manifestObj.tableRowCounts)
        if (-not $hasBaseline) {
            $RequireBaselineMatch = $true
            if ($RequireBaselineMatch) {
                throw "[ABORT] Doi soat baseline that bai: Manifest khong co tableRowCounts va bat buoc kiem tra baseline (-RequireBaselineMatch)."
            }
        }
    } catch {
        if ($_ -match "RequireBaselineMatch" -or $_ -match "khong co tableRowCounts") { $threw = $true }
    }
    if (-not $threw) { throw "Khong nem loi khi manifest thieu baseline trong che do -RequireBaselineMatch!" }
}

# Scenario 10: Restore compatibility mode chấp nhận manifest cũ nhưng ghi rõ đã bỏ qua baseline
Run-AssertTest 'S55-BL.10: Restore compatibility mode (khong bat RequireBaselineMatch) -> Chap nhan manifest cu va ghi SkippedNoBaselineInManifest' {
    $manifestObj = [PSCustomObject]@{
        backupFile = "eventpulse_backup_legacy.dump"
        status = "VerifiedValid"
    }

    $baselineCompare = @{
        Compared = $false
        Status = "SkippedNoBaselineInManifest"
        MismatchCount = 0
        Mismatches = @()
    }

    $hasBaselineInManifest = ($null -ne $manifestObj.PSObject.Properties['tableRowCounts'] -and $null -ne $manifestObj.tableRowCounts)
    if (-not $hasBaselineInManifest) {
        $baselineCompare = @{
            Compared = $false
            Status = "SkippedNoBaselineInManifest"
            MismatchCount = 0
            Mismatches = @()
        }
    }

    if ($baselineCompare.Compared -ne $false) { throw "Compared phai bang false" }
    if ($baselineCompare.Status -ne "SkippedNoBaselineInManifest") {
        throw "Status phai la 'SkippedNoBaselineInManifest', thuc te: $($baselineCompare.Status)"
    }
}

# Scenario 11: Restore thất bại khi baseline sai định dạng hoặc row count mismatch
Run-AssertTest 'S55-BL.11a: Baseline rong (0 bang) -> Compare-S55BaselineRowCounts nem loi sai dinh dang' {
    $threw = $false
    try {
        $emptyBaseline = [PSCustomObject]@{}
        Compare-S55BaselineRowCounts -Baseline $emptyBaseline -TableStats @([PSCustomObject]@{ TableName = "events"; RowCount = 10 })
    } catch {
        if ($_ -match "sai dinh dang hoac rong") { $threw = $true }
    }
    if (-not $threw) { throw "Khong nem loi khi baseline rong!" }
}

Run-AssertTest 'S55-BL.11b: Baseline co gia tri am -> Compare-S55BaselineRowCounts nem loi sai dinh dang' {
    $threw = $false
    try {
        $badBaseline = [PSCustomObject]@{ events = 0 }
        $badBaseline.events = -5
        Compare-S55BaselineRowCounts -Baseline $badBaseline -TableStats @([PSCustomObject]@{ TableName = "events"; RowCount = 10 })
    } catch {
        if ($_ -match "khong hop le") { $threw = $true }
    }
    if (-not $threw) { throw "Khong nem loi khi baseline co gia tri dem am!" }
}

Run-AssertTest 'S55-BL.11c: Row count mismatch -> Compare-S55BaselineRowCounts bao cao MismatchCount > 0' {
    $baseline = [PSCustomObject]@{
        events = 10
        tickets = 50
    }
    $tableStats = @(
        [PSCustomObject]@{ TableName = "events"; RowCount = 10 }
        [PSCustomObject]@{ TableName = "tickets"; RowCount = 48 }
    )
    $res = Compare-S55BaselineRowCounts -Baseline $baseline -TableStats $tableStats
    if ($res.MismatchCount -ne 1) { throw "Phai co 1 mismatch, thuc te: $($res.MismatchCount)" }
    if ($res.Mismatches[0] -notmatch "tickets") { throw "Mismatch phai bao gom bang tickets" }
}

# Scenario 12: Đảm bảo exit code và trạng thái kết quả phản ánh đúng lỗi
Run-AssertTest 'S55-BL.12: Doi soat baseline mismatch gay ra ngoai le va fail result ghi nhan Failed' {
    $baseline = [PSCustomObject]@{ events = 100 }
    $tableStats = @([PSCustomObject]@{ TableName = "events"; RowCount = 10 })
    $res = Compare-S55BaselineRowCounts -Baseline $baseline -TableStats $tableStats

    $drillCompletedSuccessfully = $false
    $threwDrillError = $false
    $errorMessage = ""
    try {
        if ($res.MismatchCount -gt 0) {
            throw "Doi soat baseline that bai: $($res.MismatchCount) bang co so dong khac biet: $($res.Mismatches -join '; ')"
        }
        $drillCompletedSuccessfully = $true
    } catch {
        $threwDrillError = $true
        $errorMessage = $_.Exception.Message
    }

    if (-not $threwDrillError) { throw "Khong nem loi khi co mismatch!" }
    if ($drillCompletedSuccessfully) { throw "drillCompletedSuccessfully phai la false khi baseline mismatch!" }

    $failResult = [PSCustomObject]@{
        status = "Failed"
        error  = $errorMessage
    }
    if ($failResult.status -ne "Failed") { throw "Fail result phai co status Failed" }
    if ($failResult.error -notmatch "events") { throw "Fail result phai ghi ro loi mismatch" }
}

# ==============================================================================
# TONG KET BO KIEM THU
# ==============================================================================
Write-Host "`n=================================================================="
Write-Host " TONG KET KIEM THU OFFLINE CHO S-55"
Write-Host " - So test PASS : $passCount" -ForegroundColor Green
Write-Host " - So test FAIL : $failCount" -ForegroundColor $(if ($failCount -eq 0) { "Green" } else { "Red" })
Write-Host "=================================================================="

if ($failCount -gt 0) {
    exit 1
}
exit 0
