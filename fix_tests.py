import sys, re

path = r'src\tests\EventTicketBooking.Tests\TicketGenerationAndRetrievalTests.cs'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Add mock field
if 'Mock<ITicketEmailQueue>' not in content:
    content = re.sub(
        r'        private readonly Mock<ITicketService> _mockTicketService;',
        r'        private readonly Mock<ITicketService> _mockTicketService;\n        private readonly Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue> _mockTicketEmailQueue;',
        content
    )
    # 2. Initialize mock in constructor
    content = re.sub(
        r'            _mockTicketService = new Mock<ITicketService>\(\);',
        r'            _mockTicketService = new Mock<ITicketService>();\n            _mockTicketEmailQueue = new Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>();',
        content
    )

# 3. Fix PaymentService instantiation
content = re.sub(
    r'new PaymentService\(\n?\s*context,\n?\s*_mockPaymentGateway\.Object,\n?\s*_mockPaymentLogger\.Object\n?\s*\)',
    r'new PaymentService(context, _mockPaymentGateway.Object, _mockPaymentLogger.Object, _mockTicketEmailQueue.Object, _mockTicketService.Object)',
    content
)

content = re.sub(
    r'new PaymentService\(context, _mockPaymentGateway\.Object, _mockPaymentLogger\.Object, _mockTicketService\.Object\)',
    r'new PaymentService(context, _mockPaymentGateway.Object, _mockPaymentLogger.Object, _mockTicketEmailQueue.Object, _mockTicketService.Object)',
    content
)

# And if there are any others on a single line:
content = re.sub(
    r'new PaymentService\(context, _mockPaymentGateway\.Object, _mockPaymentLogger\.Object\)',
    r'new PaymentService(context, _mockPaymentGateway.Object, _mockPaymentLogger.Object, _mockTicketEmailQueue.Object, _mockTicketService.Object)',
    content
)

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)
