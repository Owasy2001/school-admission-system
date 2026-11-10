using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SchoolAdmission.Api.Contracts;
using SchoolAdmission.Api.Data;
using SchoolAdmission.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var connectionString = ResolveConnectionString(builder.Configuration);

builder.Services.AddDbContext<AdmissionDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

builder.Services.AddScoped<AdmissionService>();
builder.Services.AddHostedService<HoldExpiryWorker>();

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
        var exception = exceptionFeature?.Error;

        if (exception is BadHttpRequestException || exception is JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new ErrorResponse(
                "INVALID_REQUEST",
                "Invalid request payload or malformed JSON."));
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new ErrorResponse(
            "INTERNAL_ERROR",
            "An unexpected server error occurred."));
    });
});

app.UseCors();

await InitializeDatabaseAsync(app.Services, app.Logger);

app.MapPost("/classes", async (HttpContext httpContext, AdmissionService service, CancellationToken ct) =>
{
    CreateClassRequest? request;
    try
    {
        request = await httpContext.Request.ReadFromJsonAsync<CreateClassRequest>(cancellationToken: ct);
    }
    catch (Exception)
    {
        return Results.Json(
            new ErrorResponse("INVALID_JSON", "Invalid JSON body. Provide valid class name and totalSeats."),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var result = await service.CreateClassAsync(request, ct);
    return result.IsSuccess
        ? Results.Json(result.Data, statusCode: result.StatusCode)
        : Results.Json(result.Error, statusCode: result.StatusCode);
});

app.MapGet("/classes", async (AdmissionService service, CancellationToken ct) =>
{
    var classes = await service.GetClassesAsync(ct);
    return Results.Ok(classes);
});

app.MapGet("/classes/{classId}", async (string classId, AdmissionService service, CancellationToken ct) =>
{
    var result = await service.GetClassByIdAsync(classId, ct);
    return result.IsSuccess
        ? Results.Ok(result.Data)
        : Results.Json(result.Error, statusCode: result.StatusCode);
});

app.MapPost("/classes/{classId}/holds", async (
    string classId,
    HttpContext httpContext,
    AdmissionService service,
    CancellationToken ct) =>
{
    var studentId = httpContext.Request.Headers["X-Student-Id"].FirstOrDefault();
    var result = await service.CreateHoldAsync(classId, studentId, ct);
    return result.IsSuccess
        ? Results.Json(result.Data, statusCode: result.StatusCode)
        : Results.Json(result.Error, statusCode: result.StatusCode);
});

app.MapGet("/holds/{holdId}", async (string holdId, AdmissionService service, CancellationToken ct) =>
{
    var result = await service.GetHoldByIdAsync(holdId, ct);
    return result.IsSuccess
        ? Results.Ok(result.Data)
        : Results.Json(result.Error, statusCode: result.StatusCode);
});

app.MapPost("/holds/{holdId}/confirm", async (
    string holdId,
    HttpContext httpContext,
    AdmissionService service,
    CancellationToken ct) =>
{
    var studentId = httpContext.Request.Headers["X-Student-Id"].FirstOrDefault();
    var result = await service.ConfirmHoldAsync(holdId, studentId, ct);
    return result.IsSuccess
        ? Results.Ok(result.Data)
        : Results.Json(result.Error, statusCode: result.StatusCode);
});

app.MapDelete("/holds/{holdId}", async (
    string holdId,
    HttpContext httpContext,
    AdmissionService service,
    CancellationToken ct) =>
{
    var studentId = httpContext.Request.Headers["X-Student-Id"].FirstOrDefault();
    var result = await service.CancelHoldAsync(holdId, studentId, ct);
    return result.IsSuccess
        ? Results.NoContent()
        : Results.Json(result.Error, statusCode: result.StatusCode);
});

app.MapGet("/me/holds", async (
    HttpContext httpContext,
    AdmissionService service,
    CancellationToken ct) =>
{
    var studentId = httpContext.Request.Headers["X-Student-Id"].FirstOrDefault();
    var result = await service.GetStudentHoldsAsync(studentId, ct);
    return result.IsSuccess
        ? Results.Ok(result.Data)
        : Results.Json(result.Error, statusCode: result.StatusCode);
});

app.Run();

static string ResolveConnectionString(IConfiguration configuration)
{
    var raw = Environment.GetEnvironmentVariable("DATABASE_URL")
              ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
              ?? Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
              ?? Environment.GetEnvironmentVariable("CONNECTION_STRING");

    if (string.IsNullOrWhiteSpace(raw) &&
        Environment.GetEnvironmentVariable("POSTGRES_HOST") == null &&
        Environment.GetEnvironmentVariable("DB_HOST") == null)
    {
        raw = configuration.GetConnectionString("DefaultConnection");
    }

    if (!string.IsNullOrWhiteSpace(raw))
    {
        if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(raw);
            var userInfo = uri.UserInfo.Split(':', 2);
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : 5432,
                Database = uri.AbsolutePath.Trim('/'),
                Username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "postgres",
                Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "postgres"
            };
            return builder.ConnectionString;
        }

        return raw;
    }

    var host = Environment.GetEnvironmentVariable("POSTGRES_HOST")
               ?? Environment.GetEnvironmentVariable("DB_HOST")
               ?? "localhost";
    var port = Environment.GetEnvironmentVariable("POSTGRES_PORT")
               ?? Environment.GetEnvironmentVariable("DB_PORT")
               ?? "5432";
    var db = Environment.GetEnvironmentVariable("POSTGRES_DB")
             ?? Environment.GetEnvironmentVariable("DB_NAME")
             ?? "school_admission";
    var user = Environment.GetEnvironmentVariable("POSTGRES_USER")
               ?? Environment.GetEnvironmentVariable("DB_USER")
               ?? "postgres";
    var pass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
               ?? Environment.GetEnvironmentVariable("DB_PASSWORD")
               ?? "postgres";

    return $"Host={host};Port={port};Database={db};Username={user};Password={pass}";
}

static async Task InitializeDatabaseAsync(IServiceProvider services, ILogger logger)
{
    const int maxRetries = 10;
    for (var attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AdmissionDbContext>();
            await db.Database.EnsureCreatedAsync();
            return;
        }
        catch (Exception ex) when (attempt < maxRetries)
        {
            logger.LogWarning(
                ex,
                "Database connection attempt {Attempt}/{MaxRetries} failed. Retrying in 1s...",
                attempt,
                maxRetries);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}

public partial class Program { }
