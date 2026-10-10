using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventTicketBooking.Tests;

public class CheckInActivationTests
{
    [Fact]
    public void MvcCanActivateCheckInControllerWithRegisteredServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase("activation"));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var factory = ActivatorUtilities.CreateFactory(typeof(TicketCheckInController), Type.EmptyTypes);
        Assert.IsType<TicketCheckInController>(factory(scope.ServiceProvider, Array.Empty<object>()));
    }
}
