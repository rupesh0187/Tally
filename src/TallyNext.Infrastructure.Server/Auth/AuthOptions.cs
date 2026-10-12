namespace TallyNext.Infrastructure.Server.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "tallynext";

    public string Audience { get; set; } = "tallynext-clients";

    /// <summary>HMAC-SHA256 key, at least 32 bytes. Supplied via environment/secret store; never committed.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Whether anyone may create an account through the API/web sign-up page.</summary>
    public bool AllowSelfRegistration { get; set; }

    /// <summary>How long a desktop may sign a user in offline after the last successful online check.</summary>
    public int OfflineGraceDays { get; set; } = 7;
}
