using AuditFlow.Contracts.Api;

namespace AuditFlow.Engagement.Api.Domain;

public enum Trigger
{
    Create,
    /// <summary>First saved edit while Draft (raised by engagement-service on behalf of an editor).</summary>
    StartWork,
    Submit,
    Approve,
    Reject,
    /// <summary>First edit after a rejection.</summary>
    StartRework
}

/// <param name="ActorRole">The actor's role on this engagement (home role for Create).</param>
/// <param name="IsAssignedReviewer">True when the actor is the reviewer chosen at creation.</param>
/// <param name="ValidationPassed">Workflow readiness result: zero blocking errors.</param>
/// <param name="HasRejectComment">A non-empty rejection comment accompanies the decision.</param>
public sealed record TransitionContext(
    EngagementRole? ActorRole,
    bool IsAssignedReviewer = false,
    bool ValidationPassed = false,
    bool HasRejectComment = false);

public sealed record TransitionResult(bool Allowed, EngagementStatus? To, string? Reason)
{
    public static TransitionResult Ok(EngagementStatus to) => new(true, to, null);
    public static TransitionResult Denied(string reason) => new(false, null, reason);
}

/// <summary>
/// The one place that decides lifecycle transitions: Draft -> In Progress -> Under Review -> Approved | Rejected,
/// Rejected -> In Progress. Pure and table-driven; persistence still guards with WHERE Status = @from AND RowVer = @ver.
/// </summary>
public static class EngagementStateMachine
{
    private static readonly Dictionary<(EngagementStatus? From, Trigger Trigger), EngagementStatus> Table = new()
    {
        [(null, Trigger.Create)] = EngagementStatus.Draft,
        [(EngagementStatus.Draft, Trigger.StartWork)] = EngagementStatus.InProgress,
        [(EngagementStatus.InProgress, Trigger.Submit)] = EngagementStatus.UnderReview,
        [(EngagementStatus.UnderReview, Trigger.Approve)] = EngagementStatus.Approved,
        [(EngagementStatus.UnderReview, Trigger.Reject)] = EngagementStatus.Rejected,
        [(EngagementStatus.Rejected, Trigger.StartRework)] = EngagementStatus.InProgress
    };

    public static TransitionResult Evaluate(EngagementStatus? from, Trigger trigger, TransitionContext context)
    {
        if (!Table.TryGetValue((from, trigger), out var to))
            return TransitionResult.Denied($"'{trigger}' is not allowed from {(from?.ToString() ?? "no state")}.");

        switch (trigger)
        {
            case Trigger.Create or Trigger.StartWork or Trigger.Submit or Trigger.StartRework:
                if (!PermissionPolicy.IsEditor(context.ActorRole))
                    return TransitionResult.Denied("Only an Auditor or Co-Auditor can do this.");
                break;

            case Trigger.Approve or Trigger.Reject:
                if (context.ActorRole != EngagementRole.Reviewer || !context.IsAssignedReviewer)
                    return TransitionResult.Denied("Only the assigned Reviewer can approve or reject.");
                break;
        }

        if (trigger == Trigger.Submit && !context.ValidationPassed)
            return TransitionResult.Denied("Required validations must pass before submission.");

        if (trigger == Trigger.Reject && !context.HasRejectComment)
            return TransitionResult.Denied("A comment is mandatory when rejecting.");

        return TransitionResult.Ok(to);
    }
}
