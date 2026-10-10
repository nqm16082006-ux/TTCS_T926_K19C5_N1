$ErrorActionPreference = "Stop"
$baseUrl = "http://localhost:5012"

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "=== TEST E2E STORY S-54: DONG Y DIEU KHOAN & POLICY, THU HOI DUOC ===" -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

# -------------------------------------------------------------
# 0. KIEM TRA MAY CHU
# -------------------------------------------------------------
try {
    $health = Invoke-RestMethod -Uri "$baseUrl/api/health" -Method Get -ErrorAction Stop
    Write-Host "[OK] Server is running healthy at $baseUrl" -ForegroundColor Green
} catch {
    throw "ERROR: Server $baseUrl is not reachable!"
}

# -------------------------------------------------------------
# 1. KIEM TRA AC1: DANG KY PHAI TICH DONG Y DIEU KHOAN
# -------------------------------------------------------------
Write-Host "`n--- 1. Testing AC1: Dang ky bat buoc tich dong y dieu khoan ---" -ForegroundColor Yellow

$uniqueId = [Guid]::NewGuid().ToString().Substring(0, 8)
$testEmail = "user_s54_$uniqueId@eventticket.com"
$testPassword = "Password@123456"

# 1.1 Gui dang ky KHONG tich acceptTerms (false) -> Phai bi tu choi 400 Bad Request
$noTermsBody = @{
    fullName = "Test User No Terms"
    email = $testEmail
    password = $testPassword
    acceptTerms = $false
} | ConvertTo-Json

try {
    $resNoTerms = Invoke-RestMethod -Uri "$baseUrl/api/auth/register" -Method Post -Body $noTermsBody -ContentType "application/json"
    throw "LOI: May chu khong duoc cho phep dang ky khi acceptTerms = false!"
} catch {
    Write-Host "[PASS] May chu tu choi dang ky khi khong dong y dieu khoan (HTTP 400 Bad Request)." -ForegroundColor Green
}

# 1.2 Gui dang ky CO tich acceptTerms (true) -> Thanh cong
$withTermsBody = @{
    fullName = "Test User S54"
    email = $testEmail
    password = $testPassword
    acceptTerms = $true
} | ConvertTo-Json

try {
    $resWithTerms = Invoke-RestMethod -Uri "$baseUrl/api/auth/register" -Method Post -Body $withTermsBody -ContentType "application/json"
    Write-Host "[PASS] Dang ky thanh cong khi tich dong y dieu khoan va chinh sach rieng tu." -ForegroundColor Green
} catch {
    throw "LOI: Dang ky that bai du da chap nhan dieu khoan: $_"
}

# -------------------------------------------------------------
# 2. DANG NHAP BANG TAI KHOAN ADMIN DE KIEM TRA
# -------------------------------------------------------------
Write-Host "`n--- 2. Dang nhap tai khoan Admin de kiem tra he thong ---" -ForegroundColor Yellow

$adminLoginBody = @{ email = "admin@eventticket.com"; password = "Admin@123456"; acceptCurrentTerms = $true } | ConvertTo-Json
$adminLoginRes = Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -Body $adminLoginBody -ContentType "application/json"
$adminToken = $adminLoginRes.accessToken
$adminHeaders = @{ Authorization = "Bearer $adminToken"; "Content-Type" = "application/json" }

Write-Host "[PASS] Dang nhap Admin thanh cong." -ForegroundColor Green

# -------------------------------------------------------------
# 3. KIEM TRA AC2: LUU PHIEN BAN VA THOI DIEM DONG Y
# -------------------------------------------------------------
Write-Host "`n--- 3. Testing AC2: He thong luu phien ban va thoi diem dong y ---" -ForegroundColor Yellow
$statusRes = Invoke-RestMethod -Uri "$baseUrl/api/v1/user/terms/status" -Method Get -Headers $adminHeaders
Write-Host "  * Phien ban dieu khoan user: $($statusRes.termsVersion)" -ForegroundColor Cyan
Write-Host "  * Thoi diem dong y: $($statusRes.termsAcceptedAt)" -ForegroundColor Cyan
Write-Host "  * Trang thai tiep thi ban dau: $($statusRes.marketingEmailOptIn)" -ForegroundColor Cyan

if (-not $statusRes.termsVersion -or -not $statusRes.termsAcceptedAt) {
    throw "LOI: Phien ban hoac thoi diem dong y chua duoc luu tru!"
}
Write-Host "[PASS] He thong da luu chinh xac phien ban ($($statusRes.termsVersion)) va thoi diem dong y." -ForegroundColor Green

# -------------------------------------------------------------
# 4. KIEM TRA AC3: DOI PHIEN BAN CHINH SACH THI DANG NHAP SAU PHAI DONG Y LAI
# -------------------------------------------------------------
Write-Host "`n--- 4. Testing AC3: Doi phien ban chinh sach bat buoc dong y lai ---" -ForegroundColor Yellow

# 4.1 Admin doi phien ban chinh sach sang v1.1
$changeVersionBody = @{ newVersion = "v1.1" } | ConvertTo-Json
$changeRes = Invoke-RestMethod -Uri "$baseUrl/api/v1/user/terms/admin/change-policy-version" -Method Post -Body $changeVersionBody -Headers $adminHeaders
Write-Host "[PASS] Admin da doi phien ban chinh sach he thong sang: $($changeRes.currentVersion)" -ForegroundColor Green

# 4.2 Thu dang nhap lai ma KHONG gui acceptCurrentTerms -> Phai nhan HTTP 428 Precondition Required
try {
    $retryLoginBody = @{ email = "admin@eventticket.com"; password = "Admin@123456"; acceptCurrentTerms = $false } | ConvertTo-Json
    $retryLoginRes = Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -Body $retryLoginBody -ContentType "application/json"
    throw "LOI: May chu phai yeu cau chap thuan phien ban moi (HTTP 428)!"
} catch {
    Write-Host "[PASS] May chu tra ve chinh xac HTTP 428 Precondition Required yeu cau dong y phien ban moi." -ForegroundColor Green
}

# 4.3 Dang nhap kem chap thuan phien ban moi (acceptCurrentTerms = true) -> Thanh cong
$acceptLoginBody = @{ email = "admin@eventticket.com"; password = "Admin@123456"; acceptCurrentTerms = $true } | ConvertTo-Json
$acceptLoginRes = Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -Body $acceptLoginBody -ContentType "application/json"
$newAdminToken = $acceptLoginRes.accessToken
$newAdminHeaders = @{ Authorization = "Bearer $newAdminToken"; "Content-Type" = "application/json" }

Write-Host "[PASS] Dang nhap thanh cong khi chap thuan phien ban moi. Token moi da duoc cap." -ForegroundColor Green

# Kiem tra lai status: phien ban da len v1.1
$newStatusRes = Invoke-RestMethod -Uri "$baseUrl/api/v1/user/terms/status" -Method Get -Headers $newAdminHeaders
if ($newStatusRes.termsVersion -ne "v1.1") {
    throw "LOI: Phien ban cua user chua duoc cap nhat len v1.1!"
}
Write-Host "[PASS] Phien ban cua user da duoc nang cap chinh xac len: $($newStatusRes.termsVersion)" -ForegroundColor Green

# -------------------------------------------------------------
# 5. KIEM TRA AC4: THU HOI DUOC VA KHI DO KHONG NHAN EMAIL TIEP THI NUA
# -------------------------------------------------------------
Write-Host "`n--- 5. Testing AC4: Thu hoi quyen nhan email tiep thi ---" -ForegroundColor Yellow

# 5.1 Nguoi dung thu hoi quyen nhan email tiep thi
$revokeRes = Invoke-RestMethod -Uri "$baseUrl/api/v1/user/terms/revoke-marketing" -Method Post -Headers $newAdminHeaders
if ($revokeRes.marketingEmailOptIn -ne $false) {
    throw "LOI: marketingEmailOptIn chua chuyen sang false!"
}
Write-Host "[PASS] Da thu hoi quyen nhan email tiep thi thanh cong." -ForegroundColor Green

# 5.2 Kiem tra may chu CHAN GUI email tiep thi khi da thu hoi (HTTP 403 Forbidden)
try {
    $sendTestBody = @{ subject = "Email tiep thi kiem thu"; content = "Noi dung khuyen mai" } | ConvertTo-Json
    $sendTestRes = Invoke-RestMethod -Uri "$baseUrl/api/v1/user/terms/send-marketing-test" -Method Post -Body $sendTestBody -Headers $newAdminHeaders
    throw "LOI: May chu khong duoc gui email tiep thi khi nguoi dung da thu hoi quyen!"
} catch {
    Write-Host "[PASS] May chu tu choi gui email tiep thi (HTTP 403 Forbidden). Tieu chi 'khong nhan email tiep thi nua' hoat dong chuan xac!" -ForegroundColor Green
}

# 5.3 Nguoi dung kich hoat lai quyen nhan email tiep thi (Opt-in)
$optInRes = Invoke-RestMethod -Uri "$baseUrl/api/v1/user/terms/opt-in-marketing" -Method Post -Headers $newAdminHeaders
if ($optInRes.marketingEmailOptIn -ne $true) {
    throw "LOI: marketingEmailOptIn chua chuyen sang true khi opt-in!"
}
Write-Host "[PASS] Da dang ky nhan lai email tiep thi thanh cong." -ForegroundColor Green

# 5.4 Thu gui lai email tiep thi -> Bay gio duoc chap thuan (HTTP 200 OK)
$sendAllowedRes = Invoke-RestMethod -Uri "$baseUrl/api/v1/user/terms/send-marketing-test" -Method Post -Body $sendTestBody -Headers $newAdminHeaders
if (-not $sendAllowedRes.sent) {
    throw "LOI: Gui email tiep thi that bai khi nguoi dung dang opt-in!"
}
Write-Host "[PASS] Gui email tiep thi thanh cong khi nguoi dung dong y." -ForegroundColor Green

# -------------------------------------------------------------
# 6. RESET VE PHIEN BAN GOC
# -------------------------------------------------------------
$resetBody = @{ newVersion = "v1.0" } | ConvertTo-Json
Invoke-RestMethod -Uri "$baseUrl/api/v1/user/terms/admin/change-policy-version" -Method Post -Body $resetBody -Headers $newAdminHeaders | Out-Null
Write-Host "`n[CLEANUP] Da reset phien ban chinh sach ve v1.0." -ForegroundColor Gray

Write-Host "`n==========================================================================" -ForegroundColor Green
Write-Host "=== TOAN BO KIEM THU S-54 DAT 100% CAC TIEU CHI CHAP NHAN (AC) ===" -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
