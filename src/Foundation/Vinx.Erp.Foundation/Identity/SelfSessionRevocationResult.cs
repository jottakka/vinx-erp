namespace Vinx.Erp.Foundation.Identity;

public enum SelfSessionRevocationState
{
    Revoked,
    NoChange,
    Unavailable
}

public sealed record SelfSessionRevocationResult(SelfSessionRevocationState State)
{
    public static SelfSessionRevocationResult Revoked() => new(SelfSessionRevocationState.Revoked);
    public static SelfSessionRevocationResult NoChange() => new(SelfSessionRevocationState.NoChange);
    public static SelfSessionRevocationResult Unavailable() => new(SelfSessionRevocationState.Unavailable);
}
