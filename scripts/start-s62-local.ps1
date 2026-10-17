param([switch]$SeedDemo)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$localDir = Join-Path $workspace '.tmp/s62-local'
New-Item -ItemType Directory -Force -Path $localDir | Out-Null
$envFile = Join-Path $localDir '.env'
if (!(Test-Path -LiteralPath $envFile)) {
    $taskBytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($taskBytes)
    $taskPassword = [Convert]::ToHexString($taskBytes)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($taskBytes)
    $taskKey = [Convert]::ToHexString($taskBytes)
    [IO.File]::WriteAllLines($envFile, @(
        'ASPNETCORE_ENVIRONMENT=Development', 'PORT=5013', 'Database__Provider=Postgres', 'Database__AutoMigrate=true',
        "ConnectionStrings__DefaultConnection=Host=127.0.0.1;Port=55432;Database=s62_local;Username=s62_local;Password=$taskPassword",
        'ConnectionStrings__Redis=127.0.0.1:56379', "JWT_KEY=$taskKey", 'PAYMENT_PROVIDER=Mock',
        'SmtpSettings__Server=127.0.0.1', 'SmtpSettings__Port=2525', 'SmtpSettings__SenderEmail=s62-local@example.invalid'))
    [IO.File]::WriteAllLines((Join-Path $localDir 'postgres.env'), @('POSTGRES_DB=s62_local', 'POSTGRES_USER=s62_local', "POSTGRES_PASSWORD=$taskPassword"))
}
$connectionLine = Get-Content -LiteralPath $envFile | Where-Object { $_ -like 'ConnectionStrings__DefaultConnection=*' }
if ($connectionLine -notmatch 'Host=127\.0\.0\.1;Port=55432;Database=s62_local;') { throw 'Refusing to use a database outside the isolated local S62 environment.' }
# Dedicated Redis avoids sharing sessions, rate limits, or locks with the cloud-connected app.
$contents = [IO.File]::ReadAllText($envFile) -replace '(?m)^ConnectionStrings__Redis=.*$', 'ConnectionStrings__Redis=127.0.0.1:56379'
[IO.File]::WriteAllText($envFile, $contents)
$pgContainer = docker ps -a --filter 'name=^event_ticket_s62_local$' --format '{{.Names}}'
if (!$pgContainer) {
    docker run -d --name event_ticket_s62_local --env-file (Join-Path $localDir 'postgres.env') -p 127.0.0.1:55432:5432 -v event_ticket_s62_local_data:/var/lib/postgresql/data postgres:16-alpine
} else { docker start event_ticket_s62_local | Out-Null }
if ($LASTEXITCODE -ne 0) { throw 'Cannot start local PostgreSQL. Start Docker Desktop first.' }
$redisContainer = docker ps -a --filter 'name=^event_ticket_s62_redis$' --format '{{.Names}}'
if (!$redisContainer) { docker run -d --name event_ticket_s62_redis -p 127.0.0.1:56379:6379 redis:7-alpine }
else { docker start event_ticket_s62_redis | Out-Null }
if ($LASTEXITCODE -ne 0) { throw 'Cannot start local Redis.' }
$pidFile = Join-Path $localDir 'app.pid'
if (Test-Path -LiteralPath $pidFile) {
    $appProcessId = [int](Get-Content -LiteralPath $pidFile)
    $previous = Get-CimInstance Win32_Process -Filter "ProcessId = $appProcessId" -ErrorAction SilentlyContinue
    if ($previous -and $previous.CommandLine.Contains((Join-Path $workspace '.tmp\')) -and $previous.CommandLine.Contains('EventTicketBooking.Api.dll')) {
        Stop-Process -Id $appProcessId
    } elseif ($previous) { throw 'Saved PID belongs to a different process; refusing to stop it.' }
}
$outputDir = Join-Path $localDir 'build'
dotnet build (Join-Path $workspace 'src/EventTicketBooking.Api') --nologo --output $outputDir
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$apiDll = Join-Path $outputDir 'EventTicketBooking.Api.dll'
$contentRoot = Join-Path $workspace 'src/EventTicketBooking.Api'
$process = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @("`"$apiDll`"", '--contentRoot', "`"$contentRoot`"") -WorkingDirectory $localDir -WindowStyle Hidden -RedirectStandardOutput (Join-Path $localDir 'app.stdout.log') -RedirectStandardError (Join-Path $localDir 'app.stderr.log') -PassThru
$process.Id | Set-Content -LiteralPath $pidFile
$healthy = $false
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    try {
        $health = Invoke-RestMethod 'http://127.0.0.1:5013/api/health' -TimeoutSec 2
        if ($health.status -eq 'Healthy') { $healthy = $true; break }
    } catch {}
    if ($process.HasExited) { throw "Local API stopped. Inspect $localDir/app.stderr.log" }
    Start-Sleep -Seconds 2
}
if (!$healthy) { throw "Local API did not become healthy. Inspect $localDir/app.stdout.log" }
Write-Host 'Local S62 API is healthy: http://localhost:5013/checkin-export.html'
if ($SeedDemo) {
    $toolOutput = Join-Path $localDir 'tool'
    dotnet build (Join-Path $workspace 'tools/S62LocalDemo') --nologo -p:BaseOutputPath="$toolOutput/"
    if ($LASTEXITCODE -ne 0) { throw 'Demo tool build failed.' }
    dotnet (Join-Path $toolOutput 'Debug/net9.0/S62LocalDemo.dll') $envFile
    if ($LASTEXITCODE -ne 0) { throw 'Local S62 smoke checks failed.' }
}
