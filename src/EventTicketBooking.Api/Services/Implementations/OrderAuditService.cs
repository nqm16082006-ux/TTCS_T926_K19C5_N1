using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class OrderAuditService : IOrderAuditService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<OrderAuditService> _logger;

        public OrderAuditService(AppDbContext context, ILogger<OrderAuditService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public void Record(
            Guid orderId,
            string entityType,
            string? entityId,
            string action,
            string? oldStatus,
            string newStatus,
            string actorType,
            string actor,
            Guid? actorUserId = null,
            string? note = null,
            DateTimeOffset? timestamp = null)
        {
            var log = new OrderAuditLog
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                EntityType = entityType?.ToUpperInvariant() ?? "ORDER",
                EntityId = entityId,
                Action = action,
                OldStatus = oldStatus,
                NewStatus = newStatus,
                ActorType = actorType?.ToUpperInvariant() ?? "SYSTEM",
                Actor = string.IsNullOrWhiteSpace(actor) ? "Unknown" : actor.Trim(),
                ActorUserId = actorUserId,
                Timestamp = timestamp ?? DateTimeOffset.UtcNow,
                Note = note
            };

            _context.OrderAuditLogs.Add(log);
            _logger.LogInformation("Ghi nhận nhật ký OrderId={OrderId}, Entity={EntityType}, Action={Action}, Status: {OldStatus} -> {NewStatus}, Actor={Actor} ({ActorType})",
                orderId, entityType, action, oldStatus, newStatus, actor, actorType);
        }

        public async Task RecordAndSaveAsync(
            Guid orderId,
            string entityType,
            string? entityId,
            string action,
            string? oldStatus,
            string newStatus,
            string actorType,
            string actor,
            Guid? actorUserId = null,
            string? note = null,
            DateTimeOffset? timestamp = null,
            CancellationToken cancellationToken = default)
        {
            Record(orderId, entityType, entityId, action, oldStatus, newStatus, actorType, actor, actorUserId, note, timestamp);
            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task<List<OrderAuditLogDto>> GetLogsByOrderIdAsync(
            Guid orderId,
            string? entityType = null,
            CancellationToken cancellationToken = default)
        {
            var query = _context.OrderAuditLogs
                .AsNoTracking()
                .Where(l => l.OrderId == orderId);

            if (!string.IsNullOrWhiteSpace(entityType) && !string.Equals(entityType, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                var upperType = entityType.Trim().ToUpperInvariant();
                query = query.Where(l => l.EntityType == upperType);
            }

            var logs = await query
                .OrderBy(l => l.Timestamp)
                .ThenBy(l => l.Id)
                .Select(l => new OrderAuditLogDto
                {
                    Id = l.Id,
                    OrderId = l.OrderId,
                    EntityType = l.EntityType,
                    EntityId = l.EntityId,
                    Action = l.Action,
                    OldStatus = l.OldStatus,
                    NewStatus = l.NewStatus,
                    ActorType = l.ActorType,
                    Actor = l.Actor,
                    ActorUserId = l.ActorUserId,
                    Timestamp = l.Timestamp,
                    Note = l.Note
                })
                .ToListAsync(cancellationToken);

            return logs;
        }
    }
}
