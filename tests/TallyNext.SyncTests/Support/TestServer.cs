using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using TallyNext.Contracts.Auth;
using TallyNext.Contracts.Sync;

namespace TallyNext.SyncTests.Support;

/// <summary>
/// The real API running in memory. Uses a throw-away SQLite file in server-test mode, or a PostgreSQL database when
/// <c>TALLYNEXT_TEST_POSTGRES</c> points at a server (a fresh database is created per instance and dropped after).
/// </summary>
public sealed class ApiServer : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tallynext-sync-" + Guid.NewGuid().ToString("N"));
    private readonly PostgresTestDatabase? _postgres = PostgresTestDatabase.TryCreate();

    public ApiServer()
    {
        Directory.CreateDirectory(_directory);
    }

    public string Provider => _postgres is null ? "Sqlite" : "Postgres";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Server", _postgres?.ConnectionString ?? $"Data Source={Path.Combine(_directory, "server.db")}");
        builder.UseSetting("Database:Provider", Provider);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Auth:AllowSelfRegistration", "true");
        builder.UseSetting("Auth:OfflineGraceDays", "7");
        builder.UseSetting("RateLimiting:AuthPermitsPerMinute", "10000");
        builder.UseSetting("Serilog:WriteTo:1:Args:path", Path.Combine(_directory, "api-.log"));
    }

    public async Task<UserProfileDto> RegisterAsync(string email, string password = TestUsers.Password)
    {
        var response = await CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest { Email = email, Password = password, DisplayName = email.Split('@')[0] });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserProfileDto>(SyncJson.Options))!;
    }

    /// <summary>An HTTP client signed in as a browser/API user (no device).</summary>
    public async Task<HttpClient> SignedInClientAsync(string email, string password = TestUsers.Password)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        response.EnsureSuccessStatusCode();
        var tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>(SyncJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            _postgres?.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort clean-up of temp files.
            }
        }
    }
}

public static class TestUsers
{
    /// <summary>Synthetic test credential, valid only against throw-away test databases.</summary>
    public const string Password = "Test-Only-Passw0rd!";
}

/// <summary>Creates and drops a scratch PostgreSQL database for one test server, if configured.</summary>
public sealed class PostgresTestDatabase : IDisposable
{
    private readonly string _adminConnection;
    private readonly string _name;

    private PostgresTestDatabase(string adminConnection)
    {
        _adminConnection = adminConnection;
        _name = "tallynext_test_" + Guid.NewGuid().ToString("N")[..12];
        using var connection = new Npgsql.NpgsqlConnection(adminConnection);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{_name}\"";
        command.ExecuteNonQuery();
        ConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(adminConnection) { Database = _name }.ToString();
    }

    public string ConnectionString { get; }

    public static PostgresTestDatabase? TryCreate()
    {
        var admin = Environment.GetEnvironmentVariable("TALLYNEXT_TEST_POSTGRES");
        return string.IsNullOrWhiteSpace(admin) ? null : new PostgresTestDatabase(admin);
    }

    public void Dispose()
    {
        Npgsql.NpgsqlConnection.ClearAllPools();
        using var connection = new Npgsql.NpgsqlConnection(_adminConnection);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)";
        command.ExecuteNonQuery();
    }
}
