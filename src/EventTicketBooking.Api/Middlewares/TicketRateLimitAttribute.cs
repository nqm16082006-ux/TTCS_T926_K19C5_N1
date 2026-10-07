namespace EventTicketBooking.Api.Middlewares;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TicketRateLimitAttribute(string policy) : Attribute
{
    public string Policy { get; } = policy;
}
