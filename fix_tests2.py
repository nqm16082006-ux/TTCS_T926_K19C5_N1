import sys, re

path = r'src\tests\EventTicketBooking.Tests\TicketGenerationAndRetrievalTests.cs'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Replace OrdersController instantiations
content = re.sub(
    r'new OrdersController\(context,\s*NullLogger<OrdersController>\.Instance,\s*new TicketService\(\)\)',
    r'new OrdersController(context, NullLogger<OrdersController>.Instance, new Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object, new TicketService())',
    content
)

# Replace PaymentService instantiations
content = re.sub(
    r'new PaymentService\(([^,]+),\s*fakeGateway,\s*NullLogger<PaymentService>\.Instance\)',
    r'new PaymentService(\1, fakeGateway, NullLogger<PaymentService>.Instance, new Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object, new TicketService())',
    content
)

content = re.sub(
    r'new PaymentService\(([^,]+),\s*fakeGateway,\s*NullLogger<PaymentService>\.Instance,\s*new Mock<ITicketEmailQueue>\(\)\.Object\)',
    r'new PaymentService(\1, fakeGateway, NullLogger<PaymentService>.Instance, new Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object, new TicketService())',
    content
)


with open(path, 'w', encoding='utf-8') as f:
    f.write(content)
