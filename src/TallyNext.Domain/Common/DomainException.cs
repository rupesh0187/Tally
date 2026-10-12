namespace TallyNext.Domain.Common;

/// <summary>
/// A business rule was violated. <see cref="Code"/> is a stable, machine-readable identifier
/// (for example <c>voucher.unbalanced</c>) that clients and the sync protocol can rely on.
/// </summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
