# Test script for S-62: Xuất danh sách khách vào cửa theo suất diễn
$ErrorActionPreference = "Stop"

Write-Host "=== 1. Đăng nhập lấy Bearer Token ===" -ForegroundColor Cyan
$loginBody = @{
    email = "admin@eventticket.com"
    password = "Admin@123456"
    acceptCurrentTerms = $true
} | ConvertTo-Json

$loginResponse = Invoke-RestMethod -Uri "http://127.0.0.1:5012/api/auth/login" -Method Post -Body $loginBody -ContentType "application/json"
$token = $loginResponse.accessToken
Write-Host "Đăng nhập thành công! Token: $($token.Substring(0, 20))..." -ForegroundColor Green

$headers = @{
    Authorization = "Bearer $token"
}

Write-Host "`n=== 2. Lấy thông tin Suất diễn ===" -ForegroundColor Cyan
$events = Invoke-RestMethod -Uri "http://127.0.0.1:5012/api/events" -Headers $headers -Method Get
$eventId = $events.data[0].id
$showtimes = Invoke-RestMethod -Uri "http://127.0.0.1:5012/api/events/$eventId/showtimes" -Headers $headers -Method Get
$showId = $showtimes.data[0].id
Write-Host "Sử dụng Showtime ID: $showId (Event: $($events.data[0].title))" -ForegroundColor Green

Write-Host "`n=== 3. Gửi HTTP GET request tới endpoint export CSV ===" -ForegroundColor Cyan
$url = "http://127.0.0.1:5012/api/shows/$showId/checkins/export-csv?includeNotCheckedIn=true"
Write-Host "Request URL: $url" -ForegroundColor Yellow

$outputFile = "D:\TTCS_T926_K19C5_N1\danh-sach-checkin-test.csv"
Invoke-WebRequest -Uri $url -Headers $headers -Method Get -OutFile $outputFile

Write-Host "`n=== 4. Kiểm tra tệp CSV đã tạo ===" -ForegroundColor Cyan
if (Test-Path $outputFile) {
    $fileInfo = Get-Item $outputFile
    Write-Host "Tệp CSV đã được tạo thành công: $($fileInfo.FullName) (Kích thước: $($fileInfo.Length) bytes)" -ForegroundColor Green
    
    # Đọc bytes để kiểm tra UTF-8 BOM (0xEF, 0xBB, 0xBF)
    $bytes = [System.IO.File]::ReadAllBytes($outputFile)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    Write-Host "Kiểm tra chuẩn UTF-8 BOM cho Excel: $(if ($hasBom) { 'HỢP LỆ (0xEF 0xBB 0xBF)' } else { 'KHÔNG CÓ BOM' })" -ForegroundColor $(if ($hasBom) { 'Green' } else { 'Red' })
    
    # Đọc nội dung tệp CSV
    $csvContent = [System.IO.File]::ReadAllText($outputFile, [System.Text.Encoding]::UTF8)
    Write-Host "`n--- NỘI DUNG TỆP CSV ---" -ForegroundColor Magenta
    Write-Host $csvContent
    Write-Host "------------------------" -ForegroundColor Magenta
} else {
    Write-Error "Không tìm thấy tệp $outputFile!"
}
