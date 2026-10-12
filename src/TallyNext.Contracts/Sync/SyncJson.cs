using System.Text.Json;
using System.Text.Json.Serialization;

namespace TallyNext.Contracts.Sync;

/// <summary>The single JSON configuration used for sync payloads on both client and server.</summary>
public static class SyncJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);

    public static T Deserialize<T>(JsonElement element) =>
        element.Deserialize<T>(Options) ?? throw new JsonException($"Payload could not be read as {typeof(T).Name}.");

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Payload could not be read as {typeof(T).Name}.");

    public static Type SnapshotTypeOf(string entityType) => entityType switch
    {
        SyncEntityTypes.Company => typeof(CompanySnapshot),
        SyncEntityTypes.AccountGroup => typeof(AccountGroupSnapshot),
        SyncEntityTypes.Ledger => typeof(LedgerSnapshot),
        SyncEntityTypes.VoucherType => typeof(VoucherTypeSnapshot),
        SyncEntityTypes.Voucher => typeof(VoucherSnapshot),
        SyncEntityTypes.Membership => typeof(MembershipSnapshot),
        SyncEntityTypes.Unit => typeof(UnitSnapshot),
        SyncEntityTypes.Godown => typeof(GodownSnapshot),
        SyncEntityTypes.StockItem => typeof(StockItemSnapshot),
        _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unknown entity type."),
    };

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new UtcDateTimeJsonConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>All sync timestamps are UTC; values without an offset are interpreted as UTC, never local time.</summary>
    private sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetDateTime();
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            };
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc));
    }
}
