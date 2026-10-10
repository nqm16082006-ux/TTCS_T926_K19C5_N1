namespace EventTicketBooking.Api.Options;

public sealed class TicketRateLimitOptions
{
    public const string SectionName = "TicketRateLimit";
    public bool Enabled { get; set; } = true;
    public bool AllowInMemoryFallback { get; set; }
    public Dictionary<string, TicketRateLimitRule> Policies { get; set; } = new()
    {
        ["seat-hold"] = new(),
        ["order-create"] = new()
    };
}

public sealed class TicketRateLimitRule
{
    public int AccountLimit { get; set; } = 5;
    public int IpLimit { get; set; } = 600;
    public int WindowSeconds { get; set; } = 60;
}
