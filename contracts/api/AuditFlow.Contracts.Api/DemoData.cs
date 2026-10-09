namespace AuditFlow.Contracts.Api;

/// <summary>
/// Deterministic demo identities so auth-service and engagement-service seeds agree without cross-service reads.
/// Dev/demo only.
/// </summary>
public static class DemoData
{
    public const string Password = "Passw0rd!";

    public sealed record DemoUser(Guid UserId, string Email, string DisplayName, EngagementRole HomeRole);

    public static readonly DemoUser Alice = new(Guid.Parse("a1000000-0000-0000-0000-000000000001"), "alice@auditflow.test", "Alice Auditor", EngagementRole.Auditor);
    public static readonly DemoUser Bob = new(Guid.Parse("a1000000-0000-0000-0000-000000000002"), "bob@auditflow.test", "Bob Co-Auditor", EngagementRole.CoAuditor);
    public static readonly DemoUser Rachel = new(Guid.Parse("a1000000-0000-0000-0000-000000000003"), "rachel@auditflow.test", "Rachel Reviewer", EngagementRole.Reviewer);
    public static readonly DemoUser Rohan = new(Guid.Parse("a1000000-0000-0000-0000-000000000004"), "rohan@auditflow.test", "Rohan Reviewer", EngagementRole.Reviewer);
    public static readonly DemoUser Victor = new(Guid.Parse("a1000000-0000-0000-0000-000000000005"), "victor@auditflow.test", "Victor Viewer", EngagementRole.Viewer);

    public static readonly IReadOnlyList<DemoUser> Users = [Alice, Bob, Rachel, Rohan, Victor];
}
