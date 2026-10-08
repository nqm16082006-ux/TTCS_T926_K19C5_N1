import sys, re

def resolve_appdbcontext():
    path = r'src\EventTicketBooking.Api\Data\AppDbContext.cs'
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    content = re.sub(r'<<<<<<< HEAD\n(.*?)\n=======\n(.*?)>>>>>>> origin/main\n', r'\1\n\2', content, flags=re.DOTALL)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(content)

def resolve_paymentservice():
    path = r'src\EventTicketBooking.Api\Services\Implementations\PaymentService.cs'
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    
    # 1. Private fields
    content = re.sub(
        r'<<<<<<< HEAD\s+private readonly EventTicketBooking\.Api\.BackgroundServices\.ITicketEmailQueue _ticketEmailQueue;\s+=======\s+private readonly ITicketService _ticketService;\s+>>>>>>> origin/main',
        r'        private readonly EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue _ticketEmailQueue;\n        private readonly ITicketService _ticketService;',
        content
    )
    
    # 2. Constructor args
    content = re.sub(
        r'<<<<<<< HEAD\s+EventTicketBooking\.Api\.BackgroundServices\.ITicketEmailQueue ticketEmailQueue\)\s+=======\s+ITicketService\? ticketService = null\)\s+>>>>>>> origin/main',
        r'            EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue ticketEmailQueue,\n            ITicketService? ticketService = null)',
        content
    )
    
    # 3. Constructor body
    content = re.sub(
        r'<<<<<<< HEAD\s+_ticketEmailQueue = ticketEmailQueue;\s+=======\s+_ticketService = ticketService \?\? new TicketService\(\);\s+>>>>>>> origin/main',
        r'            _ticketEmailQueue = ticketEmailQueue;\n            _ticketService = ticketService ?? new TicketService();',
        content
    )
    
    with open(path, 'w', encoding='utf-8') as f:
        f.write(content)

def resolve_orderscontroller():
    path = r'src\EventTicketBooking.Api\Controllers\OrdersController.cs'
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
        
    # 1. Private fields
    content = re.sub(
        r'<<<<<<< HEAD\s+private readonly EventTicketBooking\.Api\.BackgroundServices\.ITicketEmailQueue _ticketEmailQueue;\s+=======\s+private readonly EventTicketBooking\.Api\.Services\.Interfaces\.ITicketService _ticketService;\s+>>>>>>> origin/main',
        r'        private readonly EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue _ticketEmailQueue;\n        private readonly EventTicketBooking.Api.Services.Interfaces.ITicketService _ticketService;',
        content
    )
    
    # 2. Constructor args & body
    content = re.sub(
        r'<<<<<<< HEAD\s+EventTicketBooking\.Api\.BackgroundServices\.ITicketEmailQueue ticketEmailQueue\)\s+{\s+_context = context;\s+_logger = logger;\s+_ticketEmailQueue = ticketEmailQueue;\s+=======\s+EventTicketBooking\.Api\.Services\.Interfaces\.ITicketService\? ticketService = null\)\s+{\s+_context = context;\s+_logger = logger;\s+_ticketService = ticketService \?\? new EventTicketBooking\.Api\.Services\.Implementations\.TicketService\(\);\s+>>>>>>> origin/main',
        r'''            EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue ticketEmailQueue,
            EventTicketBooking.Api.Services.Interfaces.ITicketService? ticketService = null)
        {
            _context = context;
            _logger = logger;
            _ticketEmailQueue = ticketEmailQueue;
            _ticketService = ticketService ?? new EventTicketBooking.Api.Services.Implementations.TicketService();''',
        content
    )
    
    # 3. Method GetOrderTickets and ResendTickets headers
    content = re.sub(
        r'<<<<<<< HEAD\s+(/// <summary>.*?public async Task<IActionResult> ResendTickets\(Guid orderId\))\s+=======\s+(/// <summary>.*?public async Task<IActionResult> GetOrderTickets\(Guid orderId\))\s+>>>>>>> origin/main',
        r'\1',
        content, flags=re.DOTALL
    )
    
    # 4. First conflict inside method
    content = re.sub(
        r'<<<<<<< HEAD\s+=======\s+                \.Include\(o => o\.Showtime\).*?\.ThenInclude\(oi => oi\.Ticket\)\s+>>>>>>> origin/main',
        r'',
        content, flags=re.DOTALL
    )
    
    # 5. Second conflict inside method body
    content = re.sub(
        r'<<<<<<< HEAD\s+(            if \(order\.UserId != userId\).*?Yêu cầu gửi lại vé đã được tiếp nhận\.\"\)\);)\s+=======\s+(            var isAdmin = User\.IsInRole\(\"Admin\"\).*?Lấy danh sách vé thành công\.\"\)\);)\s+>>>>>>> origin/main',
        r'''\1
        }

        /// <summary>
        /// S-25: API lấy danh sách vé điện tử của đơn hàng (Read-Only).
        /// GET /api/v1/orders/{orderId}/tickets
        /// </summary>
        [HttpGet("{orderId:guid}/tickets")]
        [RequireRole]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        [ProducesResponseType(typeof(ApiResponse<List<TicketDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetOrderTickets(Guid orderId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            var order = await _context.Orders
                .AsNoTracking()
                .Include(o => o.Showtime)
                    .ThenInclude(st => st.Event)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                        .ThenInclude(s => s.SeatCategory)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Ticket)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy đơn hàng."));

\2''',
        content, flags=re.DOTALL
    )
    
    with open(path, 'w', encoding='utf-8') as f:
        f.write(content)

resolve_appdbcontext()
resolve_paymentservice()
resolve_orderscontroller()
