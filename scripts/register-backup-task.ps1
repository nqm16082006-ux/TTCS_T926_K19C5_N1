<#
.SYNOPSIS
    S-55: Đăng ký / kiểm tra lịch backup hằng ngày 02:00 (giờ địa phương máy chủ) qua Windows Task Scheduler.

.DESCRIPTION
    - CHỈ đăng ký khi truyền tường minh -Register (hoặc gỡ bằng -Remove). Mặc định chạy DRY-RUN an toàn.
    - Kiểm tra an toàn trước khi đăng ký: file script tồn tại, múi giờ máy (cảnh báo nếu không phải UTC+07:00),
      task chưa tồn tại (hoặc sẽ cập nhật nếu dùng -Force).
    - Không ghi secrets vào Task Scheduler (chỉ đường dẫn script; connection string được backup-db.ps1 đọc từ env của tài khoản chạy task).
    - In hướng dẫn cron tương đương cho Linux (tham khảo, KHÔNG tự đăng ký trùng lặp cơ chế).

    LƯU Ý MÚI GIỜ: Task Scheduler dùng giờ địa phương của máy. Để lịch 02:00 Asia/Ho_Chi_Minh,
    máy chạy task phải được đặt múi giờ UTC+07:00 (Việt Nam), hoặc điều chỉnh -DailyAt theo múi giờ máy.
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$TaskName = "EventPulse_Daily_Database_Backup",

    [Parameter()]
    [string]$ScriptPath = "",

    [Parameter()]
    [string]$BackupArgs = "",

    [Parameter()]
    [string]$DailyAt = "02:00",

    [Parameter()]
    [switch]$Register,

    [Parameter()]
    [switch]$Remove,

    [Parameter()]
    [switch]$Force,

    [Parameter()]
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot trong param default khong duoc nap dung gio trong PowerShell 5.1
# (advanced script), nen tinh duong dan o day de tuong thich ca 5.1 lan 7.x.
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path $PSScriptRoot "backup-db.ps1"
}

Write-Host "=================================================================="
Write-Host " S-55: CAU HINH LICH BACKUP HANG NGAY (WINDOWS TASK SCHEDULER)"
Write-Host "=================================================================="

# ---- 1. Xác định PowerShell engine dùng cho task -----------------------------
$pwshExe = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\pwsh.exe"
$taskExe = $null
$taskExeLabel = ""
if (Test-Path -LiteralPath $pwshExe) {
    $taskExe = $pwshExe
    $taskExeLabel = "pwsh (PowerShell 7): $taskExe"
} else {
    $taskExe = (Get-Command powershell.exe -ErrorAction SilentlyContinue).Source
    if ($taskExe) {
        $taskExeLabel = "Windows PowerShell 5.1: $taskExe"
    } else {
        throw "[LOI MOI TRUONG] Khong tim thay PowerShell engine de dang ky task."
    }
}
Write-Host "[CHECK] Engine chay task: $taskExeLabel"

# ---- 2. Kiểm tra script backup tồn tại ---------------------------------------
if (-not (Test-Path -LiteralPath $ScriptPath)) {
    throw "[LOI MOI TRUONG] Khong tim thay script backup: $ScriptPath"
}
Write-Host "[CHECK] Script backup: $(Resolve-Path -LiteralPath $ScriptPath)"

# ---- 3. Kiểm tra múi giờ máy (cảnh báo nếu không phải UTC+07:00) --------------
$tzRaw = (& tzutil /g 2>$null)
$tzOk = $false
if ($tzRaw) {
    $utcOffset = (& tzutil /g 2>$null) | Out-Null
    try {
        $tzInfo = [System.TimeZoneInfo]::Local
        $offset = $tzInfo.GetUtcOffset([datetime]::UtcNow).TotalHours
        if ($offset -eq 7.0) { $tzOk = $true }
    } catch { }
}
if ($tzOk) {
    Write-Host "[CHECK] Mui gio may: $tzRaw (UTC+07:00 — phu hop voi Asia/Ho_Chi_Minh)"
} else {
    Write-Warning "[CANH BAO MUI GIO] Mui gio may la '$tzRaw'. Lich '$DailyAt' se chay theo gio DIA PHUONG cua may, KHONG phai 02:00 gio Viet Nam. De dung 02:00 Asia/Ho_Chi_Minh, dat may ve UTC+07:00 hoac dieu chinh tham so -DailyAt."
}

# ---- 4. Kiểm tra chuỗi giờ hợp lệ ---------------------------------------------
$hourMin = $DailyAt -split ':'
if ($hourMin.Count -ne 2) { throw "[LOI CAU HINH] -DailyAt phai co dinh dang HH:mm (vi du 02:00)." }
$hour = 0; $minute = 0
if (-not ([int]::TryParse($hourMin[0], [ref]$hour)) -or -not ([int]::TryParse($hourMin[1], [ref]$minute)) -or $hour -lt 0 -or $hour -gt 23 -or $minute -lt 0 -or $minute -gt 59) {
    throw "[LOI CAU HINH] -DailyAt khong hop le: $DailyAt"
}

# ---- 5. Kiểm tra trạng thái task hiện tại ------------------------------------
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "[CHECK] Task '$TaskName' DA TON TAI (State: $($existing.State))."
    $info = Get-ScheduledTaskInfo -TaskName $TaskName
    Write-Host " - LastRunTime : $($info.LastRunTime)"
    Write-Host " - LastResult  : $($info.LastTaskResult) (0 = thanh cong)"
    Write-Host " - NextRunTime : $($info.NextRunTime)"
} else {
    Write-Host "[CHECK] Task '$TaskName' chua ton tai."
}

# ---- 6. GỠ TASK (nếu yêu cầu) -------------------------------------------------
if ($Remove) {
    if ($existing) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "[DONE] Da go task '$TaskName'."
    } else {
        Write-Host "[DONE] Task '$TaskName' khong ton tai, khong can go."
    }
    exit 0
}

# ---- 7. ĐĂNG KÝ TASK (chỉ khi -Register) ---------------------------------------
if ($Register) {
    if ($existing -and -not $Force) {
        throw "[LOI] Task '$TaskName' da ton tai. Dung -Force de ghi de cap nhat, hoac -Remove de go."
    }

    $scriptDir = Split-Path $ScriptPath -Parent
    $taskArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`""
    if (-not [string]::IsNullOrWhiteSpace($BackupArgs)) {
        $taskArgs = "$taskArgs $BackupArgs"
    }

    $action = New-ScheduledTaskAction -Execute $taskExe -Argument $taskArgs -WorkingDirectory $scriptDir
    $trigger = New-ScheduledTaskTrigger -Daily -At $DailyAt
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit (New-TimeSpan -Hours 2)

    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited

    if ($existing -and $Force) {
        Set-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal | Out-Null
        Write-Host "[DONE] Da cap nhat task '$TaskName'."
    } else {
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description "Sao luu PostgreSQL hang ngay cho EventPulse (Story S-55)" | Out-Null
        Write-Host "[DONE] Da dang ky task '$TaskName'."
    }

    $info = Get-ScheduledTaskInfo -TaskName $TaskName
    Write-Host " - NextRunTime : $($info.NextRunTime)"
    Write-Host "[LUU Y] Task chay voi quyen cua '$env:USERDOMAIN\$env:USERNAME'. Connection string duoc backup-db.ps1 doc tu bien moi truong cua phien dang nhap — dam bao 'ConnectionStrings__DefaultConnection' da duoc cau hinh trong moi truong cua tai khoan nay."
    Write-Host "[LUU Y] De kiem tra ket qua chay thu: Start-ScheduledTask -TaskName '$TaskName'; Start-Sleep 60; Get-ScheduledTaskInfo -TaskName '$TaskName' | Select LastRunTime, LastTaskResult"
    exit 0
}

# ---- 8. VERIFY (hoặc dry-run mặc định) -----------------------------------------
if ($existing) {
    $info = Get-ScheduledTaskInfo -TaskName $TaskName
    Write-Host "`n[VERIFY] Task '$TaskName':"
    Write-Host " - State        : $($existing.State)"
    Write-Host " - LastRunTime  : $($info.LastRunTime)"
    Write-Host " - LastResult   : $($info.LastTaskResult) (0 = thanh cong, 267011/267009 = chua chay/khong tim thay)"
    Write-Host " - NextRunTime  : $($info.NextRunTime)"
    Write-Host " - Actions      : $((($existing.Actions | ForEach-Object { $_.Execute + ' ' + $_.Arguments }) -join ' | '))"
} else {
    Write-Host "`n[DRY-RUN] Chua co task '$TaskName'. De dang ky, chay lai voi -Register. Duoi day la lenh tham khao:"
    Write-Host ""
    Write-Host "  .\scripts\register-backup-task.ps1 -Register -DailyAt 02:00 -BackupArgs '-LogFile artifacts\logs\backup.log'"
    Write-Host ""
    Write-Host "[LINUX/CRON] Neu database chay tren Linux, dung cron thay the (02:00 Asia/Ho_Chi_Minh):"
    Write-Host "  CRON_TZ=Asia/Ho_Chi_Minh"
    Write-Host "  0 2 * * * cd /app && pwsh -NoProfile -File ./scripts/backup-db.ps1 -LogFile /var/log/eventpulse_backup.log >> /var/log/eventpulse_backup.log 2>&1"
    Write-Host ""
    Write-Host "[KHUYEN CAO] Chi dang ky MỘT co che scheduler cho MỘT database nguon de tranh backup trung lap."
}

exit 0
