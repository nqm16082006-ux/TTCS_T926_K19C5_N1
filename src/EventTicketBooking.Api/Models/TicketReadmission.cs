namespace EventTicketBooking.Api.Models;

// An explicit additional admission never overwrites the original check-in.
public class TicketReadmission
{
    public Guid Id { get; set; }
    public Guid OrderItemId { get; set; }
    public OrderItem OrderItem { get; set; } = null!;
    public Guid StaffUserId { get; set; }
    public string StaffName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Gate { get; set; } = string.Empty;
    public DateTimeOffset AdmittedAt { get; set; }
}
