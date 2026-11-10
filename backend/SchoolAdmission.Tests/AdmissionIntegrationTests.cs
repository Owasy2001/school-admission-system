using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SchoolAdmission.Api.Contracts;
using SchoolAdmission.Api.Data;
using Xunit;

namespace SchoolAdmission.Tests;

public class TestServerHost : IAsyncDisposable
{
    private readonly SqliteConnection? _keepAliveSqliteConnection;
    private readonly string? _sqliteConnStr;
    private readonly string? _postgresTestDbName;
    private readonly string? _postgresAdminConnStr;
    private readonly string? _postgresTestConnStr;
    private readonly List<WebApplicationFactory<Program>> _factories = new();

    public bool UsingPostgres { get; private set; }

    private TestServerHost(
        SqliteConnection? keepAliveSqliteConnection,
        string? sqliteConnStr,
        string? postgresTestDbName,
        string? postgresAdminConnStr,
        string? postgresTestConnStr,
        bool usingPostgres)
    {
        _keepAliveSqliteConnection = keepAliveSqliteConnection;
        _sqliteConnStr = sqliteConnStr;
        _postgresTestDbName = postgresTestDbName;
        _postgresAdminConnStr = postgresAdminConnStr;
        _postgresTestConnStr = postgresTestConnStr;
        UsingPostgres = usingPostgres;
    }

    public static async Task<TestServerHost> CreateAsync()
    {
        var rawPg = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
                    ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres;Timeout=2;Command Timeout=5";

        try
        {
            await using var conn = new NpgsqlConnection(rawPg);
            await conn.OpenAsync();

            var testDbName = $"school_admission_test_{Guid.NewGuid():N}";
            await using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{testDbName}\";", conn))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            var testConnBuilder = new NpgsqlConnectionStringBuilder(rawPg)
            {
                Database = testDbName
            };

            return new TestServerHost(
                null,
                null,
                testDbName,
                rawPg,
                testConnBuilder.ConnectionString,
                usingPostgres: true);
        }
        catch
        {
            var sqliteConnStr = $"Data Source=admission_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=30";
            var keepAliveConn = new SqliteConnection(sqliteConnStr);
            await keepAliveConn.OpenAsync();
            return new TestServerHost(
                keepAliveConn,
                sqliteConnStr,
                null,
                null,
                null,
                usingPostgres: false);
        }
    }

    public HttpClient CreateClientInstance()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AdmissionDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AdmissionDbContext>>();
                services.RemoveAll<AdmissionDbContext>();

                if (UsingPostgres && _postgresTestConnStr is not null)
                {
                    var connStr = _postgresTestConnStr;
                    services.AddDbContext<AdmissionDbContext>(options =>
                        options.UseNpgsql(connStr));
                }
                else if (_sqliteConnStr is not null)
                {
                    var connStr = _sqliteConnStr;
                    services.AddDbContext<AdmissionDbContext>(options =>
                        options.UseSqlite(connStr));
                }
            });
        });

        _factories.Add(factory);
        return factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        if (_keepAliveSqliteConnection is not null)
        {
            await _keepAliveSqliteConnection.DisposeAsync();
        }

        if (UsingPostgres && _postgresAdminConnStr is not null && _postgresTestDbName is not null)
        {
            try
            {
                NpgsqlConnection.ClearAllPools();
                await using var conn = new NpgsqlConnection(_postgresAdminConnStr);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    $"DROP DATABASE IF EXISTS \"{_postgresTestDbName}\" WITH (FORCE);",
                    conn);
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
            }
        }
    }
}

public class AdmissionIntegrationTests
{
    private static HttpRequestMessage CreateStudentRequest(HttpMethod method, string url, string studentId)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Student-Id", studentId);
        return req;
    }

    [Fact]
    public async Task ConcurrentHoldRequests_DoNotExceedTotalSeats()
    {
        await using var host = await TestServerHost.CreateAsync();
        using var client = host.CreateClientInstance();

        var createRes = await client.PostAsJsonAsync("/classes", new
        {
            name = "Class 6 - Morning Shift",
            totalSeats = 10
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        var createdClass = await createRes.Content.ReadFromJsonAsync<ClassCreatedResponse>();
        Assert.NotNull(createdClass);

        const int totalRequests = 50;
        var tasks = Enumerable.Range(1, totalRequests).Select(async i =>
        {
            var studentId = (20152691234500000L + i).ToString();
            using var req = CreateStudentRequest(HttpMethod.Post, $"/classes/{createdClass.Id}/holds", studentId);
            return await client.SendAsync(req);
        });

        var responses = await Task.WhenAll(tasks);

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var conflicts = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(10, succeeded);
        Assert.Equal(40, conflicts);

        var classStatus = await client.GetFromJsonAsync<ClassStatusResponse>($"/classes/{createdClass.Id}");
        Assert.NotNull(classStatus);
        Assert.Equal(10, classStatus.TotalSeats);
        Assert.Equal(10, classStatus.HeldSeats);
        Assert.Equal(0, classStatus.ConfirmedSeats);
        Assert.Equal(0, classStatus.AvailableSeats);
    }

    [Fact]
    public async Task MultipleServerInstances_RespectTotalSeatLimit()
    {
        await using var host = await TestServerHost.CreateAsync();
        using var clientA = host.CreateClientInstance();
        using var clientB = host.CreateClientInstance();

        var createRes = await clientA.PostAsJsonAsync("/classes", new
        {
            name = "Class 7 - Day Shift",
            totalSeats = 8
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var createdClass = await createRes.Content.ReadFromJsonAsync<ClassCreatedResponse>();
        Assert.NotNull(createdClass);

        var tasks = Enumerable.Range(1, 30).Select(async i =>
        {
            var studentId = (20162691234500000L + i).ToString();
            var targetClient = i % 2 == 0 ? clientA : clientB;
            using var req = CreateStudentRequest(HttpMethod.Post, $"/classes/{createdClass.Id}/holds", studentId);
            return await targetClient.SendAsync(req);
        });

        var responses = await Task.WhenAll(tasks);

        Assert.Equal(8, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(22, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var statusFromB = await clientB.GetFromJsonAsync<ClassStatusResponse>($"/classes/{createdClass.Id}");
        Assert.NotNull(statusFromB);
        Assert.Equal(8, statusFromB.HeldSeats);
        Assert.Equal(0, statusFromB.AvailableSeats);
    }

    [Fact]
    public async Task ExpiredHold_ReleasesSeatAutomatically()
    {
        var previousDuration = Environment.GetEnvironmentVariable("HOLD_DURATION_SECONDS");
        Environment.SetEnvironmentVariable("HOLD_DURATION_SECONDS", "1");

        try
        {
            await using var host = await TestServerHost.CreateAsync();
            using var client = host.CreateClientInstance();

            var createRes = await client.PostAsJsonAsync("/classes", new
            {
                name = "Class 8 - Morning Shift",
                totalSeats = 1
            });
            var createdClass = await createRes.Content.ReadFromJsonAsync<ClassCreatedResponse>();
            Assert.NotNull(createdClass);

            var student1 = "20152691234567891";
            var student2 = "20152691234567892";

            using var holdReq1 = CreateStudentRequest(HttpMethod.Post, $"/classes/{createdClass.Id}/holds", student1);
            var holdRes1 = await client.SendAsync(holdReq1);
            Assert.Equal(HttpStatusCode.Created, holdRes1.StatusCode);
            var hold1 = await holdRes1.Content.ReadFromJsonAsync<HoldCreatedResponse>();
            Assert.NotNull(hold1);

            using var holdReq2Early = CreateStudentRequest(HttpMethod.Post, $"/classes/{createdClass.Id}/holds", student2);
            var holdRes2Early = await client.SendAsync(holdReq2Early);
            Assert.Equal(HttpStatusCode.Conflict, holdRes2Early.StatusCode);

            await Task.Delay(TimeSpan.FromMilliseconds(1500));

            var hold1Status = await client.GetFromJsonAsync<HoldStatusResponse>($"/holds/{hold1.HoldId}");
            Assert.NotNull(hold1Status);
            Assert.Equal("EXPIRED", hold1Status.Status);

            var classStatus = await client.GetFromJsonAsync<ClassStatusResponse>($"/classes/{createdClass.Id}");
            Assert.NotNull(classStatus);
            Assert.Equal(0, classStatus.HeldSeats);
            Assert.Equal(1, classStatus.AvailableSeats);

            using var holdReq2After = CreateStudentRequest(HttpMethod.Post, $"/classes/{createdClass.Id}/holds", student2);
            var holdRes2After = await client.SendAsync(holdReq2After);
            Assert.Equal(HttpStatusCode.Created, holdRes2After.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOLD_DURATION_SECONDS", previousDuration);
        }
    }

    [Fact]
    public async Task DuplicateHoldAndRepeatedConfirm_HandledCorrectly()
    {
        await using var host = await TestServerHost.CreateAsync();
        using var client = host.CreateClientInstance();

        var createRes = await client.PostAsJsonAsync("/classes", new
        {
            name = "Class 9 - Science Shift",
            totalSeats = 5
        });
        var createdClass = await createRes.Content.ReadFromJsonAsync<ClassCreatedResponse>();
        Assert.NotNull(createdClass);

        var student1 = "20152691234567001";
        var student2 = "20152691234567002";

        using var holdReq = CreateStudentRequest(HttpMethod.Post, $"/classes/{createdClass.Id}/holds", student1);
        var holdRes = await client.SendAsync(holdReq);
        Assert.Equal(HttpStatusCode.Created, holdRes.StatusCode);
        var hold = await holdRes.Content.ReadFromJsonAsync<HoldCreatedResponse>();
        Assert.NotNull(hold);

        using var duplicateReq = CreateStudentRequest(HttpMethod.Post, $"/classes/{createdClass.Id}/holds", student1);
        var duplicateRes = await client.SendAsync(duplicateReq);
        Assert.Equal(HttpStatusCode.Conflict, duplicateRes.StatusCode);

        using var wrongOwnerConfirm = CreateStudentRequest(HttpMethod.Post, $"/holds/{hold.HoldId}/confirm", student2);
        var wrongOwnerRes = await client.SendAsync(wrongOwnerConfirm);
        Assert.Equal(HttpStatusCode.Forbidden, wrongOwnerRes.StatusCode);

        var confirmTasks = Enumerable.Range(1, 10).Select(async _ =>
        {
            using var confirmReq = CreateStudentRequest(HttpMethod.Post, $"/holds/{hold.HoldId}/confirm", student1);
            return await client.SendAsync(confirmReq);
        });
        var confirmResponses = await Task.WhenAll(confirmTasks);
        Assert.All(confirmResponses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var classStatus = await client.GetFromJsonAsync<ClassStatusResponse>($"/classes/{createdClass.Id}");
        Assert.NotNull(classStatus);
        Assert.Equal(0, classStatus.HeldSeats);
        Assert.Equal(1, classStatus.ConfirmedSeats);
        Assert.Equal(4, classStatus.AvailableSeats);
    }

    [Fact]
    public async Task InvalidRequests_ReturnClientErrors()
    {
        await using var host = await TestServerHost.CreateAsync();
        using var client = host.CreateClientInstance();

        using var badJsonContent = new StringContent("{ invalid json ", Encoding.UTF8, "application/json");
        var badJsonRes = await client.PostAsync("/classes", badJsonContent);
        Assert.Equal(HttpStatusCode.BadRequest, badJsonRes.StatusCode);

        var emptyNameRes = await client.PostAsJsonAsync("/classes", new { name = "   ", totalSeats = 50 });
        Assert.Equal(HttpStatusCode.BadRequest, emptyNameRes.StatusCode);

        var zeroSeatsRes = await client.PostAsJsonAsync("/classes", new { name = "Class 6", totalSeats = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, zeroSeatsRes.StatusCode);

        using var badStudentReq = CreateStudentRequest(HttpMethod.Get, "/me/holds", "12345");
        var badStudentRes = await client.SendAsync(badStudentReq);
        Assert.Equal(HttpStatusCode.BadRequest, badStudentRes.StatusCode);

        var missingClassRes = await client.GetAsync("/classes/c_nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, missingClassRes.StatusCode);
    }

    [Fact]
    public async Task CancelHold_Returns204AndReleasesSeatImmediately()
    {
        await using var host = await TestServerHost.CreateAsync();
        using var client = host.CreateClientInstance();

        var createRes = await client.PostAsJsonAsync("/classes", new
        {
            name = "Class 6 - Day Shift",
            totalSeats = 1
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var createdClass = await createRes.Content.ReadFromJsonAsync<ClassCreatedResponse>();
        Assert.NotNull(createdClass);

        var student1 = "20152691234567101";
        var student2 = "20152691234567102";

        using var holdReq1 = CreateStudentRequest(HttpMethod.Post, $"/classes/{createdClass.Id}/holds", student1);
        var holdRes1 = await client.SendAsync(holdReq1);
        Assert.Equal(HttpStatusCode.Created, holdRes1.StatusCode);
        var hold1 = await holdRes1.Content.ReadFromJsonAsync<HoldCreatedResponse>();
        Assert.NotNull(hold1);

        using var wrongCancelReq = CreateStudentRequest(HttpMethod.Delete, $"/holds/{hold1.HoldId}", student2);
        var wrongCancelRes = await client.SendAsync(wrongCancelReq);
        Assert.Equal(HttpStatusCode.Forbidden, wrongCancelRes.StatusCode);

        using var cancelReq = CreateStudentRequest(HttpMethod.Delete, $"/holds/{hold1.HoldId}", student1);
        var cancelRes = await client.SendAsync(cancelReq);
        Assert.Equal(HttpStatusCode.NoContent, cancelRes.StatusCode);

        using var meHoldsReq = CreateStudentRequest(HttpMethod.Get, "/me/holds", student1);
        var meHoldsRes = await client.SendAsync(meHoldsReq);
        Assert.Equal(HttpStatusCode.OK, meHoldsRes.StatusCode);
        var myHolds = await meHoldsRes.Content.ReadFromJsonAsync<List<StudentHoldResponse>>();
        Assert.NotNull(myHolds);
        Assert.Single(myHolds);
        Assert.Equal("CANCELLED", myHolds[0].Status);

        using var holdReq2 = CreateStudentRequest(HttpMethod.Post, $"/classes/{createdClass.Id}/holds", student2);
        var holdRes2 = await client.SendAsync(holdReq2);
        Assert.Equal(HttpStatusCode.Created, holdRes2.StatusCode);
    }
}
