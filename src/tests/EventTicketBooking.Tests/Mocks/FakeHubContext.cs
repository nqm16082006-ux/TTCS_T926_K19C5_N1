using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using EventTicketBooking.Api.Hubs;
using Moq;

namespace EventTicketBooking.Tests.Mocks
{
    public static class FakeHubContext
    {
        public static IHubContext<SeatStatusHub> Create()
        {
            var mockClients = new Mock<IHubClients>();
            var mockClientProxy = new Mock<IClientProxy>();
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
            mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
            
            var mockHubContext = new Mock<IHubContext<SeatStatusHub>>();
            mockHubContext.Setup(x => x.Clients).Returns(mockClients.Object);
            mockHubContext.Setup(x => x.Groups).Returns(new Mock<IGroupManager>().Object);
            
            return mockHubContext.Object;
        }
    }
}
