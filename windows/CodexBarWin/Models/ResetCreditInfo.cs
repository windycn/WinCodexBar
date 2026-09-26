namespace CodexBarWin.Models;

public sealed class ResetCreditInfo
{
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool ExpirationKnown { get; set; }
}
