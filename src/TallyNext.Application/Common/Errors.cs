namespace TallyNext.Application.Common;

/// <summary>Base type for expected, client-reportable failures. Messages are safe to show to users.</summary>
public abstract class AppException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Input failed validation (HTTP 400).</summary>
public sealed class ValidationFailedException(string code, string message, IReadOnlyDictionary<string, string[]>? errors = null)
    : AppException(code, message)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}

/// <summary>
/// The resource does not exist <em>or the caller may not know that it exists</em> (HTTP 404).
/// Cross-company access deliberately produces this instead of 403 so identifiers cannot be probed.
/// </summary>
public sealed class NotFoundException(string entity) : AppException($"{entity}.not_found", $"The {entity.Replace('_', ' ')} was not found.");

/// <summary>Authenticated but not permitted (HTTP 403).</summary>
public sealed class ForbiddenException(string message = "You do not have permission to perform this action.")
    : AppException("forbidden", message);

/// <summary>No authenticated user (HTTP 401).</summary>
public sealed class UnauthenticatedException() : AppException("unauthenticated", "Sign in to continue.");

/// <summary>The resource changed concurrently, or a uniqueness rule was hit (HTTP 409).</summary>
public sealed class ConflictException(string code, string message, long? currentVersion = null) : AppException(code, message)
{
    public long? CurrentVersion { get; } = currentVersion;
}
