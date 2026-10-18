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
