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

    while ($elapsed -lt $TimeoutSeconds) {
        $status = & $CheckRunnerStatusBlock
        if ($status.IsRunning -eq $false) {
            $finished = $true
            $runnerExitCode = $status.ExitCode
            $rawLog = & $GetRunnerLogsBlock
            break
        }
        if ($PollIntervalMs -gt 0) {
            Start-Sleep -Milliseconds $PollIntervalMs
        }
        $elapsed++
    }

    if (-not $finished) {
        Write-Warning "[RUNNER TIMEOUT] Runner vuot qua thoi han $TimeoutSeconds giay! Tien hanh dung an toan..."
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

        return @{
            Success      = $false
            Status       = "TimedOut"
            ExitCode     = -1
            Elapsed      = $elapsed
            Log          = $rawLog
            ErrorMessage = "Runner execution timed out after $TimeoutSeconds seconds."
        }
    }

    return @{
        Success      = ($runnerExitCode -eq 0)
        Status       = if ($runnerExitCode -eq 0) { "CompletedSuccess" } else { "CompletedFailed" }
        ExitCode     = $runnerExitCode
        Elapsed      = $elapsed
        Log          = $rawLog
        ErrorMessage = if ($runnerExitCode -ne 0) { "Runner finished with non-zero exit code: $runnerExitCode" } else { $null }
    }
}
