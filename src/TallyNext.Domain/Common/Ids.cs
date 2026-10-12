using System.Security.Cryptography;

namespace TallyNext.Domain.Common;

/// <summary>
/// Identifier generation. All aggregate identifiers are UUIDv7 (RFC 9562): they can be generated
/// offline on any device without coordination, are globally unique, and sort roughly by creation time,
/// which keeps B-tree index inserts mostly append-only on the server.
/// </summary>
public static class Ids
{
    public static Guid New() => Guid.CreateVersion7();

    /// <summary>
    /// Name-based identifier (RFC 9562 version 8, SHA-256 derived) for rows whose identity is a natural key.
    /// Two parties that independently create the same logical row (for example a device and the server both
    /// recording that a user owns a company) therefore agree on its id without coordination.
    /// </summary>
    public static Guid Deterministic(string @namespace, Guid first, Guid second)
    {
        ArgumentException.ThrowIfNullOrEmpty(@namespace);
        Span<byte> input = stackalloc byte[@namespace.Length * 3 + 32];
        var written = System.Text.Encoding.UTF8.GetBytes(@namespace, input);
        first.TryWriteBytes(input[written..], bigEndian: true, out _);
        second.TryWriteBytes(input[(written + 16)..], bigEndian: true, out _);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input[..(written + 32)], hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }
}
