using AuditFlow.Contracts.Api;
using AuditFlow.Engagement.Api.Domain;
using static AuditFlow.Contracts.Api.Capabilities;
using static AuditFlow.Contracts.Api.EngagementRole;
using static AuditFlow.Contracts.Api.EngagementStatus;

namespace AuditFlow.Engagement.Tests;

public class PermissionPolicyTests
{
    public static readonly EngagementRole[] Roles = [Auditor, CoAuditor, Reviewer, Viewer];
    public static readonly EngagementStatus[] Statuses = [Draft, InProgress, UnderReview, Approved, Rejected];

    public static IEnumerable<object[]> AllCombinations()
        => Roles.SelectMany(r => Statuses.Select(s => new object[] { r, s }));

    [Theory, MemberData(nameof(AllCombinations))]
    public void Everyone_can_view(EngagementRole role, EngagementStatus status)
        => Assert.Contains(View, PermissionPolicy.CapabilitiesFor(role, status));

    [Theory, MemberData(nameof(AllCombinations))]
    public void Viewer_never_gets_attachment_or_comment_visibility_or_any_write(EngagementRole role, EngagementStatus status)
    {
        if (role != Viewer) return;

        var capabilities = PermissionPolicy.CapabilitiesFor(role, status);

        Assert.Equal([View], capabilities);
    }

    [Theory, MemberData(nameof(AllCombinations))]
    public void Reviewer_never_edits_or_uploads_or_submits(EngagementRole role, EngagementStatus status)
    {
        if (role != Reviewer) return;

        var capabilities = PermissionPolicy.CapabilitiesFor(role, status);

        Assert.DoesNotContain(Edit, capabilities);
        Assert.DoesNotContain(Attach, capabilities);
        Assert.DoesNotContain(Submit, capabilities);
    }

    [Theory, MemberData(nameof(AllCombinations))]
    public void Editors_edit_and_upload_only_while_the_engagement_is_editable(EngagementRole role, EngagementStatus status)
    {
        if (role is not (Auditor or CoAuditor)) return;
        var editable = status is Draft or InProgress or Rejected;

        var capabilities = PermissionPolicy.CapabilitiesFor(role, status);

        Assert.Equal(editable, capabilities.Contains(Edit));
        Assert.Equal(editable, capabilities.Contains(Attach));
    }

    [Theory]
    [InlineData(Auditor)]
    [InlineData(CoAuditor)]
    public void Auditor_and_CoAuditor_have_identical_permissions_in_every_status(EngagementRole role)
    {
        foreach (var status in Statuses)
            Assert.Equal(PermissionPolicy.CapabilitiesFor(Auditor, status), PermissionPolicy.CapabilitiesFor(role, status));
    }

    [Fact]
    public void Submit_is_only_available_to_editors_while_InProgress()
    {
        foreach (var status in Statuses)
            Assert.Equal(status == InProgress, PermissionPolicy.CapabilitiesFor(Auditor, status).Contains(Submit));
    }

    [Fact]
    public void Reviewer_can_comment_and_decide_only_while_UnderReview()
    {
        foreach (var status in Statuses)
        {
            var capabilities = PermissionPolicy.CapabilitiesFor(Reviewer, status);
            Assert.Equal(status == UnderReview, capabilities.Contains(Comment));
            Assert.Equal(status == UnderReview, capabilities.Contains(Review));
        }
    }

    [Fact]
    public void Approved_is_read_only_for_everyone()
    {
        foreach (var role in Roles)
        {
            var capabilities = PermissionPolicy.CapabilitiesFor(role, Approved);
            Assert.DoesNotContain(Edit, capabilities);
            Assert.DoesNotContain(Attach, capabilities);
            Assert.DoesNotContain(Submit, capabilities);
            Assert.DoesNotContain(Comment, capabilities);
            Assert.DoesNotContain(Review, capabilities);
        }
    }

    [Fact]
    public void Non_viewers_can_see_attachments_and_comments()
    {
        foreach (var role in new[] { Auditor, CoAuditor, Reviewer })
        {
            var capabilities = PermissionPolicy.CapabilitiesFor(role, InProgress);
            Assert.Contains(AttachView, capabilities);
            Assert.Contains(CommentView, capabilities);
        }
    }

    [Theory]
    [InlineData(Auditor, true)]
    [InlineData(CoAuditor, true)]
    [InlineData(Reviewer, false)]
    [InlineData(Viewer, false)]
    public void Only_editor_home_roles_can_create_engagements(EngagementRole homeRole, bool expected)
        => Assert.Equal(expected, PermissionPolicy.CanCreateEngagement(homeRole));

    [Fact]
    public void Quick_actions_follow_role_and_status()
    {
        Assert.Equal([EngagementActions.Open, EngagementActions.Edit], PermissionPolicy.AllowedActionsFor(Auditor, Draft));
        Assert.Equal([EngagementActions.Open, EngagementActions.Edit, EngagementActions.Submit], PermissionPolicy.AllowedActionsFor(Auditor, InProgress));
        Assert.Equal([EngagementActions.Open, EngagementActions.Review], PermissionPolicy.AllowedActionsFor(Reviewer, UnderReview));
        Assert.Equal([EngagementActions.Open], PermissionPolicy.AllowedActionsFor(Reviewer, Approved));
        Assert.Equal([EngagementActions.Open], PermissionPolicy.AllowedActionsFor(Viewer, InProgress));
    }
}
