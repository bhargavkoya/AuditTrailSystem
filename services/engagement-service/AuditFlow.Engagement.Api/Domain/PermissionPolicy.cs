using AuditFlow.Contracts.Api;

namespace AuditFlow.Engagement.Api.Domain;

/// <summary>
/// The single permission matrix (role x status -> capabilities). Other services ask engagement-service's access endpoint;
/// none of them re-implement this.
/// </summary>
public static class PermissionPolicy
{
    public static bool IsEditor(EngagementRole? role) => role is EngagementRole.Auditor or EngagementRole.CoAuditor;

    /// <summary>Statuses in which content can change: Under Review locks it, Approved is final.</summary>
    public static bool IsEditable(EngagementStatus status)
        => status is EngagementStatus.Draft or EngagementStatus.InProgress or EngagementStatus.Rejected;

    /// <summary>Home roles allowed to create engagements (global capability, not per engagement).</summary>
    public static bool CanCreateEngagement(EngagementRole homeRole) => IsEditor(homeRole);

    public static IReadOnlyList<string> CapabilitiesFor(EngagementRole role, EngagementStatus status)
    {
        var capabilities = new List<string> { Capabilities.View };

        if (IsEditor(role) && IsEditable(status))
        {
            capabilities.Add(Capabilities.Edit);
            capabilities.Add(Capabilities.Attach);
        }

        if (IsEditor(role) && status == EngagementStatus.InProgress)
            capabilities.Add(Capabilities.Submit);

        // Viewers never see attachments or comments.
        if (role != EngagementRole.Viewer)
        {
            capabilities.Add(Capabilities.AttachView);
            capabilities.Add(Capabilities.CommentView);
        }

        if (role == EngagementRole.Reviewer && status == EngagementStatus.UnderReview)
        {
            capabilities.Add(Capabilities.Comment);
            capabilities.Add(Capabilities.Review);
        }

        return capabilities;
    }

    /// <summary>Quick actions for the dashboard row / engagement header, derived from capabilities.</summary>
    public static IReadOnlyList<string> AllowedActionsFor(EngagementRole role, EngagementStatus status)
    {
        var capabilities = CapabilitiesFor(role, status);
        var actions = new List<string> { EngagementActions.Open };

        if (capabilities.Contains(Capabilities.Edit)) actions.Add(EngagementActions.Edit);
        if (capabilities.Contains(Capabilities.Submit)) actions.Add(EngagementActions.Submit);
        if (capabilities.Contains(Capabilities.Review)) actions.Add(EngagementActions.Review);

        return actions;
    }
}
