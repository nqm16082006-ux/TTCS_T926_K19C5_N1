$baseUrl = "http://127.0.0.1:5012"
$showId = "32c8e735-8a78-4fae-83b1-a621dfe6d0d2"
$outputFile = "d:\TTCS_T926_K19C5_N1\danh-sach-checkin.csv"

Write-Host "1. Testing backend connectivity on $baseUrl..."
try {
    $health = Invoke-RestMethod -Uri "$baseUrl/api/health" -Method Get -TimeoutSec 5
    Write-Host "-> Backend status: $($health.status) (Environment: $($health.environment))"
} catch {
    Write-Error "Cannot connect to backend: $_"
    exit 1
}

Write-Host "2. Logging in as Admin to get JWT token..."
$loginBody = @{
    email = "admin@eventticket.com"
    password = "Admin@123456"
    acceptCurrentTerms = $true
} | ConvertTo-Json

$loginRes = Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -Body $loginBody -ContentType "application/json"
$token = $loginRes.accessToken
Write-Host "-> Login success! User: $($loginRes.user.email) (Role: $($loginRes.user.role))"

Write-Host "3. Calling API Export CSV for Show ID: $showId..."
$exportUrl = "$baseUrl/api/shows/$showId/checkins/export-csv?includeNotCheckedIn=true"
Write-Host "-> Request URL: $exportUrl"

$headers = @{
    "Authorization" = "Bearer $token"
}

try {
    $response = Invoke-WebRequest -Uri $exportUrl -Method Get -Headers $headers -OutFile $outputFile
    Write-Host "-> Saved file to: $outputFile"
} catch {
    $errRes = $_.Exception.Response
    if ($errRes) {
        $reader = New-Object System.IO.StreamReader($errRes.GetResponseStream())
        $errText = $reader.ReadToEnd()
        Write-Host "-> Response code: $([int]$errRes.StatusCode) - $errText"
    } else {
        Write-Error "Request error: $_"
    }
}

if (Test-Path $outputFile) {
    $fileInfo = Get-Item $outputFile
    Write-Host "4. File details:"
    Write-Host "   Size: $($fileInfo.Length) bytes"
    Write-Host "   Created: $($fileInfo.LastWriteTime)"
    Write-Host "5. Content preview:"
    Get-Content $outputFile -Encoding UTF8 | Select-Object -First 20 | ForEach-Object { Write-Host "   $_" }
}
