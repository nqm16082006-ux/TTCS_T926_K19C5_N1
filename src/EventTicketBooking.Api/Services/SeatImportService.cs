using System.Text.Json;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EventTicketBooking.Api.Services
{
    public class SeatImportService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly AppDbContext _dbContext;

        public SeatImportService(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<SeatImportResultDto> ImportAsync(
            Guid showtimeId,
            Stream jsonStream,
            CancellationToken cancellationToken = default)
        {
            List<SeatImportItemDto>? items;

            try
            {
                items = await JsonSerializer.DeserializeAsync<List<SeatImportItemDto>>(
                    jsonStream,
                    JsonOptions,
                    cancellationToken);
            }
            catch (JsonException ex)
            {
                // Báo lỗi định dạng kèm vị trí ký tự
                var errors = new List<SeatImportError>
                {
                    new SeatImportError { ErrorMessage = $"Lỗi định dạng JSON tại dòng {ex.LineNumber}, ký tự {ex.BytePositionInLine}: {ex.Message}" }
                };
                throw new EventTicketBooking.Api.Exceptions.SeatValidationException(errors);
            }

            var validationErrors = SeatMapValidator.Validate(items);
            if (validationErrors.Count > 0)
            {
                throw new EventTicketBooking.Api.Exceptions.SeatValidationException(validationErrors);
            }

            var validatedItems = new List<(string Row, int SeatNumber, string Category)>(items!.Count);
            foreach (var item in items)
            {
                validatedItems.Add((item.Row!.Trim(), item.SeatNumber, item.Category!.Trim()));
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var showtimeExists = await _dbContext.Showtimes
                    .AsNoTracking()
                    .AnyAsync(showtime => showtime.Id == showtimeId, cancellationToken);

                if (!showtimeExists)
                {
                    throw new KeyNotFoundException("The requested showtime does not exist.");
                }

                var existingCategories = await _dbContext.SeatCategories
                    .Where(category => category.ShowtimeId == showtimeId)
                    .ToListAsync(cancellationToken);

                var categoriesByName = existingCategories
                    .GroupBy(category => category.Name, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

                var existingSeatRows = await _dbContext.Seats
                    .AsNoTracking()
                    .Where(seat => seat.ShowtimeId == showtimeId)
                    .Select(seat => new { seat.Row, seat.SeatNumber })
                    .ToListAsync(cancellationToken);

                var existingSeatKeys = existingSeatRows
                    .Select(seat => (seat.Row, seat.SeatNumber))
                    .ToHashSet();

                if (validatedItems.Any(item => existingSeatKeys.Contains((item.Row, item.SeatNumber))))
                {
                    throw new InvalidOperationException("One or more seats already exist for this showtime.");
                }

                var newCategories = new List<SeatCategory>();
                var seats = new List<Seat>(validatedItems.Count);

                foreach (var item in validatedItems)
                {
                    if (!categoriesByName.TryGetValue(item.Category, out var category))
                    {
                        category = new SeatCategory
                        {
                            ShowtimeId = showtimeId,
                            Name = item.Category
                        };

                        categoriesByName.Add(item.Category, category);
                        newCategories.Add(category);
                    }

                    seats.Add(new Seat
                    {
                        ShowtimeId = showtimeId,
                        SeatCategoryId = category.Id,
                        Row = item.Row,
                        SeatNumber = item.SeatNumber
                    });
                }

                _dbContext.SeatCategories.AddRange(newCategories);
                _dbContext.Seats.AddRange(seats);

                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return new SeatImportResultDto
                {
                    ShowtimeId = showtimeId,
                    ImportedSeatCount = seats.Count,
                    CreatedCategoryCount = newCategories.Count
                };
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is PostgresException postgresException &&
                postgresException.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await RollbackAsync(transaction);
                throw new InvalidOperationException("One or more seats already exist for this showtime.", ex);
            }
            catch
            {
                await RollbackAsync(transaction);
                throw;
            }
        }

        private static async Task RollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original import failure if rollback also fails.
            }
        }
    }
}
