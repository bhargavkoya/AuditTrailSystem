using AuditFlow.Contracts.Api;
using AuditFlow.Engagement.Api.Domain;
using static AuditFlow.Contracts.Api.EngagementRole;
using static AuditFlow.Contracts.Api.EngagementStatus;

namespace AuditFlow.Engagement.Tests;

public class StateMachineTests
{
    private static TransitionContext Editor => new(Auditor);
    private static TransitionContext AssignedReviewer(bool comment = true) => new(Reviewer, IsAssignedReviewer: true, HasRejectComment: comment);

    // --- the valid lifecycle ---

    [Fact] public void Create_starts_in_Draft() => AssertOk(null, Trigger.Create, Editor, Draft);
    [Fact] public void First_work_moves_Draft_to_InProgress() => AssertOk(Draft, Trigger.StartWork, Editor, InProgress);
    [Fact] public void Submit_moves_InProgress_to_UnderReview() => AssertOk(InProgress, Trigger.Submit, new(CoAuditor, ValidationPassed: true), UnderReview);
    [Fact] public void Approve_moves_UnderReview_to_Approved() => AssertOk(UnderReview, Trigger.Approve, AssignedReviewer(), Approved);
    [Fact] public void Reject_moves_UnderReview_to_Rejected() => AssertOk(UnderReview, Trigger.Reject, AssignedReviewer(), Rejected);
    [Fact] public void Rework_moves_Rejected_back_to_InProgress() => AssertOk(Rejected, Trigger.StartRework, Editor, InProgress);

    // --- structural rules: everything not in the table is denied ---

    private static readonly HashSet<(EngagementStatus?, Trigger)> Valid =
    [
        (null, Trigger.Create), (Draft, Trigger.StartWork), (InProgress, Trigger.Submit),
        (UnderReview, Trigger.Approve), (UnderReview, Trigger.Reject), (Rejected, Trigger.StartRework)
    ];

    public static IEnumerable<object?[]> InvalidPairs()
    {
        var states = new EngagementStatus?[] { null, Draft, InProgress, UnderReview, Approved, Rejected };
        foreach (var from in states)
            foreach (var trigger in Enum.GetValues<Trigger>())
                if (!Valid.Contains((from, trigger)))
                    yield return [from, trigger];
    }

    [Theory, MemberData(nameof(InvalidPairs))]
    public void Every_transition_outside_the_table_is_denied_even_with_full_rights(EngagementStatus? from, Trigger trigger)
    {
        var allRights = new TransitionContext(Reviewer, IsAssignedReviewer: true, ValidationPassed: true, HasRejectComment: true);

        var result = EngagementStateMachine.Evaluate(from, trigger, allRights);

        Assert.False(result.Allowed);
        Assert.Null(result.To);
    }

    [Fact]
    public void Approved_is_terminal()
    {
        foreach (var trigger in Enum.GetValues<Trigger>())
            Assert.False(EngagementStateMachine.Evaluate(Approved, trigger, new(Auditor, true, true, true)).Allowed);
    }

    [Fact]
    public void Rejected_cannot_be_resubmitted_without_rework()
        => Assert.False(EngagementStateMachine.Evaluate(Rejected, Trigger.Submit, new(Auditor, ValidationPassed: true)).Allowed);

    // --- who may trigger ---

    [Theory]
    [InlineData(Reviewer)]
    [InlineData(Viewer)]
    public void Only_editors_can_create_start_submit_or_rework(EngagementRole role)
    {
        var ctx = new TransitionContext(role, IsAssignedReviewer: true, ValidationPassed: true, HasRejectComment: true);

        Assert.False(EngagementStateMachine.Evaluate(null, Trigger.Create, ctx).Allowed);
        Assert.False(EngagementStateMachine.Evaluate(Draft, Trigger.StartWork, ctx).Allowed);
        Assert.False(EngagementStateMachine.Evaluate(InProgress, Trigger.Submit, ctx).Allowed);
        Assert.False(EngagementStateMachine.Evaluate(Rejected, Trigger.StartRework, ctx).Allowed);
    }

    [Theory]
    [InlineData(Auditor)]
    [InlineData(CoAuditor)]
    [InlineData(Viewer)]
    public void Only_the_reviewer_can_approve_or_reject(EngagementRole role)
    {
        var ctx = new TransitionContext(role, IsAssignedReviewer: true, HasRejectComment: true);

        Assert.False(EngagementStateMachine.Evaluate(UnderReview, Trigger.Approve, ctx).Allowed);
        Assert.False(EngagementStateMachine.Evaluate(UnderReview, Trigger.Reject, ctx).Allowed);
    }

    [Fact]
    public void A_reviewer_who_is_not_the_assigned_one_cannot_decide()
    {
        var other = new TransitionContext(Reviewer, IsAssignedReviewer: false, HasRejectComment: true);

        Assert.False(EngagementStateMachine.Evaluate(UnderReview, Trigger.Approve, other).Allowed);
        Assert.False(EngagementStateMachine.Evaluate(UnderReview, Trigger.Reject, other).Allowed);
    }

    // --- preconditions ---

    [Fact]
    public void Submit_requires_validations_to_pass()
    {
        var result = EngagementStateMachine.Evaluate(InProgress, Trigger.Submit, new(Auditor, ValidationPassed: false));

        Assert.False(result.Allowed);
        Assert.Contains("validations", result.Reason);
    }

    [Fact]
    public void Reject_requires_a_comment()
    {
        var result = EngagementStateMachine.Evaluate(UnderReview, Trigger.Reject, AssignedReviewer(comment: false));

        Assert.False(result.Allowed);
        Assert.Contains("comment", result.Reason);
    }

    [Fact]
    public void Approve_does_not_need_a_comment()
        => AssertOk(UnderReview, Trigger.Approve, AssignedReviewer(comment: false), Approved);

    private static void AssertOk(EngagementStatus? from, Trigger trigger, TransitionContext ctx, EngagementStatus expected)
    {
        var result = EngagementStateMachine.Evaluate(from, trigger, ctx);
        Assert.True(result.Allowed, result.Reason);
        Assert.Equal(expected, result.To);
    }
}
