using Microsoft.EntityFrameworkCore;
using SchoolAdmission.Api.Contracts;
using SchoolAdmission.Api.Data;
using SchoolAdmission.Api.Domain;

namespace SchoolAdmission.Api.Services;

public class ServiceResult<T>
{
    public bool IsSuccess { get; set; }
    public int StatusCode { get; set; }
    public T? Data { get; set; }
    public ErrorResponse? Error { get; set; }

    public static ServiceResult<T> Success(T data, int statusCode = 200)
    {
        return new ServiceResult<T>
        {
            IsSuccess = true,
            StatusCode = statusCode,
            Data = data
        };
    }

    public static ServiceResult<T> Failure(int statusCode, string error, string message)
    {
        return new ServiceResult<T>
        {
            IsSuccess = false,
            StatusCode = statusCode,
            Error = new ErrorResponse(error, message)
        };
    }
}

public class AdmissionService
{
    private readonly AdmissionDbContext _db;
    private readonly IConfiguration _config;
    private static readonly SemaphoreSlim SqliteFallbackLock = new(1, 1);

    public AdmissionService(AdmissionDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public int GetHoldDurationSeconds()
    {
        var rawValue = Environment.GetEnvironmentVariable("HOLD_DURATION_SECONDS")
                       ?? _config["HOLD_DURATION_SECONDS"];

        if (int.TryParse(rawValue, out var seconds) && seconds > 0)
        {
            return seconds;
        }

        return 600;
    }

    public static bool TryValidateStudentId(string? studentIdHeader, out string normalizedId, out string errorMessage)
    {
        normalizedId = string.Empty;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(studentIdHeader))
        {
            errorMessage = "X-Student-Id header is required (17-digit Birth Registration Number).";
            return false;
        }

        var trimmed = studentIdHeader.Trim();
        if (trimmed.Length != 17 || !trimmed.All(char.IsDigit))
        {
            errorMessage = "X-Student-Id must be a valid 17-digit numeric Birth Registration Number.";
            return false;
        }

        normalizedId = trimmed;
        return true;
    }

    public static string FormatUtcIso(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    public async Task<int> ExpireElapsedHoldsAsync(CancellationToken ct = default)
    {
        var isNpgsql = _db.Database.IsNpgsql();
        if (!isNpgsql)
        {
            await SqliteFallbackLock.WaitAsync(ct);
        }

        try
        {
            var now = DateTime.UtcNow;
            return await _db.Holds
                .Where(h => h.Status == HoldStatuses.Held && h.ExpiresAt <= now)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(h => h.Status, HoldStatuses.Expired),
                    ct);
        }
        finally
        {
            if (!isNpgsql)
            {
                SqliteFallbackLock.Release();
            }
        }
    }

    public async Task<ServiceResult<ClassCreatedResponse>> CreateClassAsync(
        CreateClassRequest? request,
        CancellationToken ct = default)
    {
        if (request == null)
        {
            return ServiceResult<ClassCreatedResponse>.Failure(
                400, "INVALID_INPUT", "Request body is required.");
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return ServiceResult<ClassCreatedResponse>.Failure(
                400, "INVALID_NAME", "Class name cannot be empty.");
        }

        if (name.Length > 256)
        {
            return ServiceResult<ClassCreatedResponse>.Failure(
                400, "INVALID_NAME", "Class name cannot exceed 256 characters.");
        }

        if (request.TotalSeats == null || request.TotalSeats <= 0 || request.TotalSeats > 100000)
        {
            return ServiceResult<ClassCreatedResponse>.Failure(
                400, "INVALID_SEATS", "totalSeats must be a positive integer greater than 0.");
        }

        var classId = "c_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var schoolClass = new SchoolClass
        {
            Id = classId,
            Name = name,
            TotalSeats = request.TotalSeats.Value,
            CreatedAt = DateTime.UtcNow
        };

        _db.Classes.Add(schoolClass);
        await _db.SaveChangesAsync(ct);

        var response = new ClassCreatedResponse
        {
            Id = schoolClass.Id,
            Name = schoolClass.Name,
            TotalSeats = schoolClass.TotalSeats
        };

        return ServiceResult<ClassCreatedResponse>.Success(response, 201);
    }

    public async Task<List<ClassStatusResponse>> GetClassesAsync(CancellationToken ct = default)
    {
        await ExpireElapsedHoldsAsync(ct);
        var now = DateTime.UtcNow;

        var rows = await _db.Classes
            .AsNoTracking()
            .OrderBy(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.TotalSeats,
                HeldSeats = c.Holds.Count(h => h.Status == HoldStatuses.Held && h.ExpiresAt > now),
                ConfirmedSeats = c.Holds.Count(h => h.Status == HoldStatuses.Confirmed)
            })
            .ToListAsync(ct);

        var result = new List<ClassStatusResponse>();
        foreach (var row in rows)
        {
            var available = Math.Max(0, row.TotalSeats - row.HeldSeats - row.ConfirmedSeats);
            result.Add(new ClassStatusResponse
            {
                Id = row.Id,
                Name = row.Name,
                TotalSeats = row.TotalSeats,
                HeldSeats = row.HeldSeats,
                ConfirmedSeats = row.ConfirmedSeats,
                AvailableSeats = available
            });
        }

        return result;
    }

    public async Task<ServiceResult<ClassStatusResponse>> GetClassByIdAsync(
        string classId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(classId))
        {
            return ServiceResult<ClassStatusResponse>.Failure(
                400, "INVALID_CLASS_ID", "A valid classId is required.");
        }

        await ExpireElapsedHoldsAsync(ct);
        var now = DateTime.UtcNow;

        var row = await _db.Classes
            .AsNoTracking()
            .Where(c => c.Id == classId)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.TotalSeats,
                HeldSeats = c.Holds.Count(h => h.Status == HoldStatuses.Held && h.ExpiresAt > now),
                ConfirmedSeats = c.Holds.Count(h => h.Status == HoldStatuses.Confirmed)
            })
            .FirstOrDefaultAsync(ct);

        if (row == null)
        {
            return ServiceResult<ClassStatusResponse>.Failure(
                404, "CLASS_NOT_FOUND", "The requested class was not found.");
        }

        var available = Math.Max(0, row.TotalSeats - row.HeldSeats - row.ConfirmedSeats);
        var response = new ClassStatusResponse
        {
            Id = row.Id,
            Name = row.Name,
            TotalSeats = row.TotalSeats,
            HeldSeats = row.HeldSeats,
            ConfirmedSeats = row.ConfirmedSeats,
            AvailableSeats = available
        };

        return ServiceResult<ClassStatusResponse>.Success(response);
    }

    public async Task<ServiceResult<HoldCreatedResponse>> CreateHoldAsync(
        string classId,
        string? studentIdHeader,
        CancellationToken ct = default)
    {
        if (!TryValidateStudentId(studentIdHeader, out var studentId, out var errorMsg))
        {
            return ServiceResult<HoldCreatedResponse>.Failure(400, "INVALID_STUDENT_ID", errorMsg);
        }

        if (string.IsNullOrWhiteSpace(classId))
        {
            return ServiceResult<HoldCreatedResponse>.Failure(
                400, "INVALID_CLASS_ID", "A valid classId is required.");
        }

        var isNpgsql = _db.Database.IsNpgsql();
        if (!isNpgsql)
        {
            await SqliteFallbackLock.WaitAsync(ct);
        }

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            SchoolClass? targetClass;
            if (isNpgsql)
            {
                targetClass = await _db.Classes
                    .FromSqlInterpolated($"SELECT * FROM classes WHERE id = {classId} FOR UPDATE")
                    .AsNoTracking()
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                targetClass = await _db.Classes
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == classId, ct);
            }

            if (targetClass == null)
            {
                await transaction.RollbackAsync(ct);
                return ServiceResult<HoldCreatedResponse>.Failure(
                    404, "CLASS_NOT_FOUND", "The requested class was not found.");
            }

            var now = DateTime.UtcNow;

            await _db.Holds
                .Where(h => h.ClassId == classId && h.Status == HoldStatuses.Held && h.ExpiresAt <= now)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(h => h.Status, HoldStatuses.Expired),
                    ct);

            var hasActiveOrConfirmedHold = await _db.Holds
                .AsNoTracking()
                .AnyAsync(h =>
                    h.ClassId == classId &&
                    h.StudentId == studentId &&
                    (h.Status == HoldStatuses.Confirmed ||
                     (h.Status == HoldStatuses.Held && h.ExpiresAt > now)),
                    ct);

            if (hasActiveOrConfirmedHold)
            {
                await transaction.RollbackAsync(ct);
                return ServiceResult<HoldCreatedResponse>.Failure(
                    409,
                    "DUPLICATE_APPLICATION",
                    "This student already has an active hold or confirmed admission in this class.");
            }

            var occupiedSeats = await _db.Holds
                .AsNoTracking()
                .CountAsync(h =>
                    h.ClassId == classId &&
                    (h.Status == HoldStatuses.Confirmed ||
                     (h.Status == HoldStatuses.Held && h.ExpiresAt > now)),
                    ct);

            if (occupiedSeats >= targetClass.TotalSeats)
            {
                await transaction.RollbackAsync(ct);
                return ServiceResult<HoldCreatedResponse>.Failure(
                    409,
                    "NO_SEATS_AVAILABLE",
                    "No seats are currently available in this class.");
            }

            var holdDurationSeconds = GetHoldDurationSeconds();
            var expiresAt = now.AddSeconds(holdDurationSeconds);
            var holdId = "h_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var hold = new SeatHold
            {
                HoldId = holdId,
                ClassId = classId,
                StudentId = studentId,
                Status = HoldStatuses.Held,
                ExpiresAt = expiresAt,
                CreatedAt = now
            };

            _db.Holds.Add(hold);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            var response = new HoldCreatedResponse
            {
                HoldId = hold.HoldId,
                ClassId = hold.ClassId,
                StudentId = hold.StudentId,
                Status = hold.Status,
                ExpiresAt = FormatUtcIso(hold.ExpiresAt)
            };

            return ServiceResult<HoldCreatedResponse>.Success(response, 201);
        }
        finally
        {
            if (!isNpgsql)
            {
                SqliteFallbackLock.Release();
            }
        }
    }

    public async Task<ServiceResult<HoldStatusResponse>> GetHoldByIdAsync(
        string holdId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(holdId))
        {
            return ServiceResult<HoldStatusResponse>.Failure(
                400, "INVALID_HOLD_ID", "A valid holdId is required.");
        }

        await ExpireElapsedHoldsAsync(ct);

        var hold = await _db.Holds
            .AsNoTracking()
            .Include(h => h.SchoolClass)
            .FirstOrDefaultAsync(h => h.HoldId == holdId, ct);

        if (hold == null)
        {
            return ServiceResult<HoldStatusResponse>.Failure(
                404, "HOLD_NOT_FOUND", "The requested application hold was not found.");
        }

        var effectiveStatus = hold.Status == HoldStatuses.Held && hold.ExpiresAt <= DateTime.UtcNow
            ? HoldStatuses.Expired
            : hold.Status;

        var response = new HoldStatusResponse
        {
            HoldId = hold.HoldId,
            ClassId = hold.ClassId,
            Status = effectiveStatus,
            ExpiresAt = FormatUtcIso(hold.ExpiresAt),
            StudentId = hold.StudentId,
            ClassName = hold.SchoolClass?.Name ?? string.Empty
        };

        return ServiceResult<HoldStatusResponse>.Success(response);
    }

    public async Task<ServiceResult<HoldConfirmResponse>> ConfirmHoldAsync(
        string holdId,
        string? studentIdHeader,
        CancellationToken ct = default)
    {
        if (!TryValidateStudentId(studentIdHeader, out var studentId, out var errorMsg))
        {
            return ServiceResult<HoldConfirmResponse>.Failure(400, "INVALID_STUDENT_ID", errorMsg);
        }

        if (string.IsNullOrWhiteSpace(holdId))
        {
            return ServiceResult<HoldConfirmResponse>.Failure(
                400, "INVALID_HOLD_ID", "A valid holdId is required.");
        }

        var isNpgsql = _db.Database.IsNpgsql();
        if (!isNpgsql)
        {
            await SqliteFallbackLock.WaitAsync(ct);
        }

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            SeatHold? hold;
            if (isNpgsql)
            {
                hold = await _db.Holds
                    .FromSqlInterpolated($"SELECT * FROM holds WHERE hold_id = {holdId} FOR UPDATE")
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                hold = await _db.Holds
                    .FirstOrDefaultAsync(h => h.HoldId == holdId, ct);
            }

            if (hold == null)
            {
                await transaction.RollbackAsync(ct);
                return ServiceResult<HoldConfirmResponse>.Failure(
                    404, "HOLD_NOT_FOUND", "The requested application hold was not found.");
            }

            if (!string.Equals(hold.StudentId, studentId, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(ct);
                return ServiceResult<HoldConfirmResponse>.Failure(
                    403, "FORBIDDEN", "You can only confirm your own application hold.");
            }

            if (hold.Status == HoldStatuses.Confirmed)
            {
                await transaction.RollbackAsync(ct);
                return ServiceResult<HoldConfirmResponse>.Success(new HoldConfirmResponse
                {
                    HoldId = hold.HoldId,
                    Status = HoldStatuses.Confirmed
                });
            }

            var now = DateTime.UtcNow;
            if (hold.Status == HoldStatuses.Held && hold.ExpiresAt <= now)
            {
                hold.Status = HoldStatuses.Expired;
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return ServiceResult<HoldConfirmResponse>.Failure(
                    409, "HOLD_EXPIRED", "This hold has expired and cannot be confirmed.");
            }

            if (hold.Status != HoldStatuses.Held)
            {
                await transaction.RollbackAsync(ct);
                return ServiceResult<HoldConfirmResponse>.Failure(
                    409,
                    "INVALID_HOLD_STATUS",
                    $"Cannot confirm an application with status '{hold.Status}'.");
            }

            hold.Status = HoldStatuses.Confirmed;
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return ServiceResult<HoldConfirmResponse>.Success(new HoldConfirmResponse
            {
                HoldId = hold.HoldId,
                Status = HoldStatuses.Confirmed
            });
        }
        finally
        {
            if (!isNpgsql)
            {
                SqliteFallbackLock.Release();
            }
        }
    }

    public async Task<ServiceResult<bool>> CancelHoldAsync(
        string holdId,
        string? studentIdHeader,
        CancellationToken ct = default)
    {
        if (!TryValidateStudentId(studentIdHeader, out var studentId, out var errorMsg))
        {
            return ServiceResult<bool>.Failure(400, "INVALID_STUDENT_ID", errorMsg);
        }

        if (string.IsNullOrWhiteSpace(holdId))
        {
            return ServiceResult<bool>.Failure(
                400, "INVALID_HOLD_ID", "A valid holdId is required.");
        }

        var isNpgsql = _db.Database.IsNpgsql();
        if (!isNpgsql)
        {
            await SqliteFallbackLock.WaitAsync(ct);
        }

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            SeatHold? hold;
            if (isNpgsql)
            {
                hold = await _db.Holds
                    .FromSqlInterpolated($"SELECT * FROM holds WHERE hold_id = {holdId} FOR UPDATE")
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                hold = await _db.Holds
                    .FirstOrDefaultAsync(h => h.HoldId == holdId, ct);
            }

            if (hold == null)
            {
                await transaction.RollbackAsync(ct);
                return ServiceResult<bool>.Failure(
                    404, "HOLD_NOT_FOUND", "The requested application hold was not found.");
            }

            if (!string.Equals(hold.StudentId, studentId, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(ct);
                return ServiceResult<bool>.Failure(
                    403, "FORBIDDEN", "You can only cancel your own application hold.");
            }

            var now = DateTime.UtcNow;
            if (hold.Status == HoldStatuses.Held && hold.ExpiresAt <= now)
            {
                hold.Status = HoldStatuses.Expired;
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return ServiceResult<bool>.Failure(
                    409, "HOLD_EXPIRED", "This hold has already expired.");
            }

            if (hold.Status != HoldStatuses.Held)
            {
                await transaction.RollbackAsync(ct);
                return ServiceResult<bool>.Failure(
                    409,
                    "INVALID_HOLD_STATUS",
                    $"Cannot cancel an application with status '{hold.Status}'.");
            }

            hold.Status = HoldStatuses.Cancelled;
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return ServiceResult<bool>.Success(true, 204);
        }
        finally
        {
            if (!isNpgsql)
            {
                SqliteFallbackLock.Release();
            }
        }
    }

    public async Task<ServiceResult<List<StudentHoldResponse>>> GetStudentHoldsAsync(
        string? studentIdHeader,
        CancellationToken ct = default)
    {
        if (!TryValidateStudentId(studentIdHeader, out var studentId, out var errorMsg))
        {
            return ServiceResult<List<StudentHoldResponse>>.Failure(400, "INVALID_STUDENT_ID", errorMsg);
        }

        await ExpireElapsedHoldsAsync(ct);
        var now = DateTime.UtcNow;

        var holds = await _db.Holds
            .AsNoTracking()
            .Include(h => h.SchoolClass)
            .Where(h => h.StudentId == studentId)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync(ct);

        var result = new List<StudentHoldResponse>();
        foreach (var h in holds)
        {
            var effectiveStatus = h.Status == HoldStatuses.Held && h.ExpiresAt <= now
                ? HoldStatuses.Expired
                : h.Status;

            result.Add(new StudentHoldResponse
            {
                HoldId = h.HoldId,
                ClassId = h.ClassId,
                ClassName = h.SchoolClass?.Name ?? string.Empty,
                Status = effectiveStatus,
                ExpiresAt = FormatUtcIso(h.ExpiresAt)
            });
        }

        return ServiceResult<List<StudentHoldResponse>>.Success(result);
    }
}
