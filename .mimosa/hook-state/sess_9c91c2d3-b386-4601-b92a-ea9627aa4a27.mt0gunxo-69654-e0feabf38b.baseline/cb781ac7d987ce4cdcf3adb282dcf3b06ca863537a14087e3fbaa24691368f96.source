namespace Vinx.Erp.Foundation.Identity;

public enum SessionAdmissionState
{
    Admitted,
    Active,
    Denied,
    Unavailable
}

public sealed record SessionAdmissionResult(SessionAdmissionState State, string? Reason = null)
{
    public static SessionAdmissionResult Admitted() => new(SessionAdmissionState.Admitted);
    public static SessionAdmissionResult Active() => new(SessionAdmissionState.Active);
    public static SessionAdmissionResult Denied(string reason) => new(SessionAdmissionState.Denied, reason);
    public static SessionAdmissionResult Unavailable() => new(SessionAdmissionState.Unavailable);
}
