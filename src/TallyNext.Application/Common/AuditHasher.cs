using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TallyNext.Domain.Audit;

namespace TallyNext.Application.Common;

/// <summary>
/// Tamper-evidence for the audit log. Each entry's hash covers its own content and the previous entry's hash,
/// forming a chain per company. Fields are length-prefixed so that no two different records can produce the same
/// canonical text. Timestamps are hashed at microsecond precision (the precision PostgreSQL stores).
/// </summary>
public static class AuditHasher
{
    /// <summary>The previous-hash value of the first entry in every chain.</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Compute(AuditEntry entry, long chainSequence, string previousHash)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var builder = new StringBuilder(256);
        Append(builder, "v1");
        Append(builder, entry.Id.ToString("D"));
        Append(builder, entry.CompanyId?.ToString("D"));
        Append(builder, chainSequence.ToString(CultureInfo.InvariantCulture));
        Append(builder, entry.OccurredAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture));
        Append(builder, entry.UserId?.ToString("D"));
        Append(builder, entry.DeviceId?.ToString("D"));
        Append(builder, ((int)entry.Source).ToString(CultureInfo.InvariantCulture));
        Append(builder, entry.Action);
        Append(builder, entry.EntityType);
        Append(builder, entry.EntityId?.ToString("D"));
        Append(builder, entry.Summary);
        Append(builder, entry.ChangesJson);
        Append(builder, entry.CorrelationId);
        Append(builder, entry.ChangeId?.ToString("D"));
        Append(builder, previousHash);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(digest);
    }

    private static void Append(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:;");
            return;
        }

        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }
}
