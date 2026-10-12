using TallyNext.Application.Abstractions;
using TallyNext.Domain.Audit;
using TallyNext.Domain.Common;

namespace TallyNext.Infrastructure.Local.Session;

public sealed record SignedInUser(Guid UserId, string Email, string? DisplayName, bool IsOffline, DateTime SignedInAtUtc);

/// <summary>
/// The signed-in user of the desktop application (one per running app). Raises <see cref="Changed"/> on sign-in,
/// sign-out and company switch so the UI can refresh.
/// </summary>
public sealed class DeviceSession
{
    private readonly Lock _gate = new();
    private SignedInUser? _user;
    private Guid? _companyId;

    public event Action? Changed;

    public SignedInUser? User
    {
        get
        {
            lock (_gate)
            {
                return _user;
            }
        }
    }

    public Guid? CurrentCompanyId
    {
        get
        {
            lock (_gate)
            {
                return _companyId;
            }
        }
    }

    public Guid DeviceId { get; internal set; }

    public void SignIn(SignedInUser user)
    {
        lock (_gate)
        {
            _user = user;
            _companyId = null;
        }

        Changed?.Invoke();
    }

    public void SignOut()
    {
        lock (_gate)
        {
            _user = null;
            _companyId = null;
        }

        Changed?.Invoke();
    }

    public void SelectCompany(Guid? companyId)
    {
        lock (_gate)
        {
            _companyId = companyId;
        }

        Changed?.Invoke();
    }
}

/// <summary>Execution context for use cases running on the desktop: the session user on this device.</summary>
internal sealed class DeviceExecutionContext(DeviceSession session) : IExecutionContext
{
    private readonly string _correlationId = Ids.New().ToString("N");

    public Guid? UserId => session.User?.UserId;

    public string? UserEmail => session.User?.Email;

    public string? UserDisplayName => session.User?.DisplayName;

    public Guid? DeviceId => session.DeviceId == Guid.Empty ? null : session.DeviceId;

    public string CorrelationId => _correlationId;

    public AuditSource AuditSource => AuditSource.Device;
}
