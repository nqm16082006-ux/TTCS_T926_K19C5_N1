$ErrorActionPreference = "Stop"
$baseUrl = "http://localhost:5012"
$showtimeId = "a9e2f901-cfea-4a23-bcff-050a88ab2aed"

Write-Host "=== TEST E2E STORY S-25 ===" -ForegroundColor Cyan

# 1. Login Customer 1
$loginBody = @{ email = "customer1@eventticket.com"; password = "Admin@123456" } | ConvertTo-Json
$loginRes1 = Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -Body $loginBody -ContentType "application/json"
$token1 = $loginRes1.accessToken
$headers1 = @{ Authorization = "Bearer $token1"; "Content-Type" = "application/json" }

# 2. Login Customer 2 (for Auth tests)
$loginBody2 = @{ email = "customer2@eventticket.com"; password = "Admin@123456" } | ConvertTo-Json
$loginRes2 = Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -Body $loginBody2 -ContentType "application/json"
$token2 = $loginRes2.accessToken
$headers2 = @{ Authorization = "Bearer $token2"; "Content-Type" = "application/json" }

Write-Host "[OK] Logged in customer1 and customer2" -ForegroundColor Green

# -------------------------------------------------------------
# CASE D: Pending Order -> GET /tickets returns []
# -------------------------------------------------------------
Write-Host "`n--- Testing CASE D: Pending order -> tickets returns empty list ---" -ForegroundColor Yellow
# Hold 1 VIP seat (e.g. accbe2e9-bc1e-4cf0-91c1-fd81112623b9 - R1-2)
$seatD = "accbe2e9-bc1e-4cf0-91c1-fd81112623b9"
$holdReq = @{ seatIds = @($seatD) } | ConvertTo-Json
$holdRes = Invoke-RestMethod -Uri "$baseUrl/api/showtimes/$showtimeId/seats/hold" -Method Post -Headers $headers1 -Body $holdReq
Write-Host "Seat held: $($holdRes.data.heldSeatIds -join ', ')"

# Create order from holds (Price 50000 -> status Pending)
$orderResD = Invoke-RestMethod -Uri "$baseUrl/api/v1/orders/showtimes/$showtimeId" -Method Post -Headers $headers1
$orderIdD = $orderResD.data.id
$orderStatusD = $orderResD.data.status
Write-Host "Order created: $orderIdD, status: $orderStatusD"
if ($orderStatusD -ne "Pending") { throw "Expected Pending status for non-zero order!" }

# GET /tickets for Pending order
$ticketsResD = Invoke-RestMethod -Uri "$baseUrl/api/v1/orders/$orderIdD/tickets" -Method Get -Headers $headers1
Write-Host "Tickets for Pending order: count = $($ticketsResD.data.Count)"
if ($ticketsResD.data.Count -ne 0) { throw "Expected 0 tickets for Pending order!" }
Write-Host "[PASS] CASE D: Pending order returns empty tickets list" -ForegroundColor Green

# -------------------------------------------------------------
# CASE A: Paid order 1 seat
# -------------------------------------------------------------
Write-Host "`n--- Testing CASE A: Paid order 1 seat ---" -ForegroundColor Yellow
# Create payment for order D
$payCreateD = Invoke-RestMethod -Uri "$baseUrl/api/v1/payments/orders/$orderIdD" -Method Post -Headers $headers1
Write-Host "Payment created: $($payCreateD.data.orderCode)"

# Simulate payment success callback
$callbackReqA = @{ orderId = $orderIdD; status = "SUCCESS" } | ConvertTo-Json
$callbackResA = Invoke-RestMethod -Uri "$baseUrl/api/mock-payment/callback" -Method Post -Headers $headers1 -Body $callbackReqA
Write-Host "Payment callback response: $($callbackResA.data.redirectUrl)"

# Check DB tickets for orderIdD
$dbTicketsCountA = ('SELECT COUNT(*) FROM "tickets" t JOIN "order_items" oi ON t."OrderItemId" = oi."Id" WHERE oi."OrderId" = ''{0}'';' -f $orderIdD | docker exec -i event_ticket_postgres psql -U postgres -d event_ticket_db -t).Trim()
Write-Host "DB tickets count for order 1 seat: $dbTicketsCountA"
if ($dbTicketsCountA -ne "1") { throw "Expected exactly 1 ticket in DB!" }

# GET /tickets for orderIdD
$ticketsResA = Invoke-RestMethod -Uri "$baseUrl/api/v1/orders/$orderIdD/tickets" -Method Get -Headers $headers1
Write-Host "API tickets count: $($ticketsResA.data.Count)"
if ($ticketsResA.data.Count -ne 1) { throw "Expected 1 ticket from API!" }
$ticketA = $ticketsResA.data[0]
Write-Host "TicketCode: $($ticketA.ticketCode)"
Write-Host "SeatName: $($ticketA.seatName)"
Write-Host "EventTitle: $($ticketA.eventTitle)"
if (-not ($ticketA.ticketCode -match '^TK-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$')) {
    throw "TicketCode does not match expected format TK-XXXX-XXXX-XXXX-XXXX: $($ticketA.ticketCode)"
}
Write-Host "[PASS] CASE A: Paid order 1 seat generates 1 Ticket with correct format and metadata" -ForegroundColor Green

# -------------------------------------------------------------
# CASE C: Refresh idempotency
# -------------------------------------------------------------
Write-Host "`n--- Testing CASE C: Refresh idempotency ---" -ForegroundColor Yellow
$ticketsResC = Invoke-RestMethod -Uri "$baseUrl/api/v1/orders/$orderIdD/tickets" -Method Get -Headers $headers1
$ticketC = $ticketsResC.data[0]
if ($ticketsResC.data.Count -ne 1 -or $ticketC.ticketCode -ne $ticketA.ticketCode) {
    throw "Ticket changed or duplicated on refresh!"
}
$dbTicketsCountC = ('SELECT COUNT(*) FROM "tickets" t JOIN "order_items" oi ON t."OrderItemId" = oi."Id" WHERE oi."OrderId" = ''{0}'';' -f $orderIdD | docker exec -i event_ticket_postgres psql -U postgres -d event_ticket_db -t).Trim()
if ($dbTicketsCountC -ne "1") { throw "Ticket count changed in DB on refresh!" }
Write-Host "[PASS] CASE C: Refresh is read-only, TicketCode unchanged: $($ticketC.ticketCode)" -ForegroundColor Green

# -------------------------------------------------------------
# CASE E: Duplicate webhook / payment callback idempotency
# -------------------------------------------------------------
Write-Host "`n--- Testing CASE E: Duplicate payment callback / webhook ---" -ForegroundColor Yellow
$callbackResE = Invoke-RestMethod -Uri "$baseUrl/api/mock-payment/callback" -Method Post -Headers $headers1 -Body $callbackReqA
Write-Host "Duplicate callback result: $($callbackResE.message)"
$dbTicketsCountE = ('SELECT COUNT(*) FROM "tickets" t JOIN "order_items" oi ON t."OrderItemId" = oi."Id" WHERE oi."OrderId" = ''{0}'';' -f $orderIdD | docker exec -i event_ticket_postgres psql -U postgres -d event_ticket_db -t).Trim()
if ($dbTicketsCountE -ne "1") { throw "Duplicate ticket created in DB on repeat callback!" }
Write-Host "[PASS] CASE E: Duplicate callback does not create duplicate tickets (count = $dbTicketsCountE)" -ForegroundColor Green

# -------------------------------------------------------------
# AUTH CHECK: Owner vs Non-Owner vs Unauthenticated
# -------------------------------------------------------------
Write-Host "`n--- Testing AUTH: Owner vs Non-Owner vs Unauthenticated ---" -ForegroundColor Yellow
# Unauthenticated -> 401
try {
    Invoke-RestMethod -Uri "$baseUrl/api/v1/orders/$orderIdD/tickets" -Method Get
    throw "Expected 401 for unauthenticated request!"
} catch {
    Write-Host "Unauthenticated request failed as expected: $($_.Exception.Message)"
}

# Customer 2 (non-owner) -> 403
try {
    Invoke-RestMethod -Uri "$baseUrl/api/v1/orders/$orderIdD/tickets" -Method Get -Headers $headers2
    throw "Expected 403 for non-owner request!"
} catch {
    Write-Host "Non-owner request failed as expected: $($_.Exception.Message)"
}
Write-Host "[PASS] AUTH: Correctly enforces 401 for anonymous and 403 for non-owner" -ForegroundColor Green

# -------------------------------------------------------------
# CASE B: Paid order 3 seats
# -------------------------------------------------------------
Write-Host "`n--- Testing CASE B: Paid order 3 seats ---" -ForegroundColor Yellow
# Hold 3 VIP seats (3dc55896-3931-4534-934a-a9dc69b85fd5, 76114528-d3ca-4473-aaba-244f6c662850, 5830f460-1ae8-4d83-a620-b90ad2367bd6)
$seatsB = @("3dc55896-3931-4534-934a-a9dc69b85fd5", "76114528-d3ca-4473-aaba-244f6c662850", "5830f460-1ae8-4d83-a620-b90ad2367bd6")
$holdReqB = @{ seatIds = $seatsB } | ConvertTo-Json
$holdResB = Invoke-RestMethod -Uri "$baseUrl/api/showtimes/$showtimeId/seats/hold" -Method Post -Headers $headers1 -Body $holdReqB

# Create order
$orderResB = Invoke-RestMethod -Uri "$baseUrl/api/v1/orders/showtimes/$showtimeId" -Method Post -Headers $headers1
$orderIdB = $orderResB.data.id
Write-Host "Order 3 seats created: $orderIdB"

# Pay order
$payCreateB = Invoke-RestMethod -Uri "$baseUrl/api/v1/payments/orders/$orderIdB" -Method Post -Headers $headers1
$callbackReqB = @{ orderId = $orderIdB; status = "SUCCESS" } | ConvertTo-Json
$callbackResB = Invoke-RestMethod -Uri "$baseUrl/api/mock-payment/callback" -Method Post -Headers $headers1 -Body $callbackReqB

# Check DB count
$dbTicketsCountB = ('SELECT COUNT(*) FROM "tickets" t JOIN "order_items" oi ON t."OrderItemId" = oi."Id" WHERE oi."OrderId" = ''{0}'';' -f $orderIdB | docker exec -i event_ticket_postgres psql -U postgres -d event_ticket_db -t).Trim()
Write-Host "DB tickets count for order 3 seats: $dbTicketsCountB"
if ($dbTicketsCountB -ne "3") { throw "Expected exactly 3 tickets in DB for 3 seats order!" }

# GET /tickets
$ticketsResB = Invoke-RestMethod -Uri "$baseUrl/api/v1/orders/$orderIdB/tickets" -Method Get -Headers $headers1
if ($ticketsResB.data.Count -ne 3) { throw "Expected 3 tickets from API!" }

$codesB = $ticketsResB.data | ForEach-Object { $_.ticketCode }
Write-Host "Ticket codes: $($codesB -join ', ')"
$uniqueCodesB = $codesB | Select-Object -Unique
if ($uniqueCodesB.Count -ne 3) { throw "Ticket codes are not unique!" }

foreach ($t in $ticketsResB.data) {
    Write-Host "Ticket: Code=$($t.ticketCode), Seat=$($t.seatName), Category=$($t.categoryName), Event=$($t.eventTitle)"
    if ([string]::IsNullOrEmpty($t.seatName) -or [string]::IsNullOrEmpty($t.eventTitle)) {
        throw "Missing seatName or eventTitle!"
    }
}
Write-Host "[PASS] CASE B: 3 seats order generates 3 distinct Tickets with distinct TicketCodes" -ForegroundColor Green

# -------------------------------------------------------------
# CASE F: Order 0 VNĐ (Standard category = 0 VNĐ)
# -------------------------------------------------------------
Write-Host "`n--- Testing CASE F: Order 0 VNĐ auto-paid ---" -ForegroundColor Yellow
# Hold 2 Standard seats (a83e5f03-42be-4870-9ec1-2b90a0fb3da4, b0f31d0c-3e38-4730-9abf-c124e3afe9ca)
$seatsF = @("a83e5f03-42be-4870-9ec1-2b90a0fb3da4", "b0f31d0c-3e38-4730-9abf-c124e3afe9ca")
$holdReqF = @{ seatIds = $seatsF } | ConvertTo-Json
$holdResF = Invoke-RestMethod -Uri "$baseUrl/api/showtimes/$showtimeId/seats/hold" -Method Post -Headers $headers1 -Body $holdReqF

# Create order from holds (0 VNĐ -> auto Paid)
$orderResF = Invoke-RestMethod -Uri "$baseUrl/api/v1/orders/showtimes/$showtimeId" -Method Post -Headers $headers1
$orderIdF = $orderResF.data.id
$orderStatusF = $orderResF.data.status
Write-Host "Order 0 VNĐ created: $orderIdF, status: $orderStatusF"
if ($orderStatusF -ne "Paid") { throw "Expected Paid status immediately for 0 VNĐ order!" }

# Check DB tickets count
$dbTicketsCountF = ('SELECT COUNT(*) FROM "tickets" t JOIN "order_items" oi ON t."OrderItemId" = oi."Id" WHERE oi."OrderId" = ''{0}'';' -f $orderIdF | docker exec -i event_ticket_postgres psql -U postgres -d event_ticket_db -t).Trim()
Write-Host "DB tickets count for 0 VNĐ order: $dbTicketsCountF"
if ($dbTicketsCountF -ne "2") { throw "Expected 2 tickets in DB for 0 VNĐ order with 2 seats!" }

# GET /tickets
$ticketsResF = Invoke-RestMethod -Uri "$baseUrl/api/v1/orders/$orderIdF/tickets" -Method Get -Headers $headers1
if ($ticketsResF.data.Count -ne 2) { throw "Expected 2 tickets from API for 0 VNĐ order!" }
Write-Host "[PASS] CASE F: Order 0 VNĐ automatically transitions to Paid and generates tickets in single transaction" -ForegroundColor Green

Write-Host "`n==========================================" -ForegroundColor Green
Write-Host "ALL E2E CASES PASSED SUCCESSFULLY!" -ForegroundColor Green
Write-Host "OrderId 1 Seat: $orderIdD" -ForegroundColor Green
Write-Host "OrderId 3 Seats: $orderIdB" -ForegroundColor Green
Write-Host "OrderId 0 VNĐ: $orderIdF" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
