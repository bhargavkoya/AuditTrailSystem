using System.Net;
using System.Net.Http.Json;
using AuditFlow.BuildingBlocks.Access;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.BuildingBlocks.Versioning;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AuditFlow.BuildingBlocks.Tests;

public class IfMatchTests
{
    private static HttpRequest RequestWith(string? ifMatch)
    {
        var context = new DefaultHttpContext();
        if (ifMatch is not null) context.Request.Headers.IfMatch = ifMatch;
        return context.Request;
    }

    [Fact]
    public void Missing_header_is_a_validation_error() =>
        Assert.Throws<ValidationFailedException>(() => RequestWith(null).GetIfMatchToken());

    [Fact]
    public void Malformed_token_is_a_validation_error() =>
        Assert.Throws<ValidationFailedException>(() => RequestWith("\"not base64!\"").GetIfMatchToken());

    [Fact]
    public void Quoted_etag_is_unwrapped_and_decodes()
    {
        var token = RowVersion.ToToken([0, 0, 0, 0, 0, 0, 7, 209]);

        var request = RequestWith(RowVersion.ToETag(token));

        Assert.Equal(token, request.GetIfMatchToken());
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 7, 209 }, request.GetIfMatchVersion());
    }
}

public class EngagementAccessTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly EngagementAccessDto Access =
        new("ENG-1", EngagementRole.Auditor, EngagementStatus.InProgress, "AAAA", [Capabilities.View, Capabilities.Edit]);

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(respond());
        }
    }

    private static (EngagementAccessClient Client, StubHandler Handler, EngagementAccessCache Cache, FakeTimeProvider Clock) Create(
        Func<HttpResponseMessage> respond)
    {
        var clock = new FakeTimeProvider();
        var cache = new EngagementAccessCache(clock);
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://engagement") };
        var user = new Mock<ICurrentUser>();
        user.SetupGet(u => u.UserId).Returns(UserA);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer abc";
        return (new EngagementAccessClient(http, cache, user.Object, new HttpContextAccessor { HttpContext = context }), handler, cache, clock);
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = JsonContent.Create(Access) };

    [Fact]
    public async Task Miss_calls_engagement_service_with_the_callers_token_then_hits_the_cache()
    {
        var (client, handler, _, _) = Create(Ok);

        var first = await client.GetAccessAsync("ENG-1", default);
        var second = await client.GetAccessAsync("ENG-1", default);

        Assert.Equal(Access.Capabilities, first.Capabilities);
        Assert.Equal("ENG-1", second.EngagementId);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("Bearer abc", handler.LastAuthorization);
    }

    [Fact]
    public async Task Entry_expires_after_the_ttl()
    {
        var (client, handler, _, clock) = Create(Ok);
        await client.GetAccessAsync("ENG-1", default);

        clock.Advance(EngagementAccessCache.Ttl + TimeSpan.FromSeconds(1));
        await client.GetAccessAsync("ENG-1", default);

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Eviction_forces_a_refetch()
    {
        var (client, handler, cache, _) = Create(Ok);
        await client.GetAccessAsync("ENG-1", default);

        cache.Evict("ENG-1");
        await client.GetAccessAsync("ENG-1", default);

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Status_changed_handler_evicts_only_that_engagement()
    {
        var (client, handler, cache, _) = Create(Ok);
        await client.GetAccessAsync("ENG-1", default);
        cache.Set("ENG-2", UserA, Access with { EngagementId = "ENG-2" });
        var envelope = EventEnvelope<EngagementStatusChangedPayload>.Create(
            EventTypes.EngagementStatusChanged, "ENG-1",
            new EngagementStatusChangedPayload(EngagementStatus.Draft, EngagementStatus.InProgress, "WorkStarted", "AAAA"));

        await new AccessCacheEvictionHandler(cache).HandleAsync(envelope, null!, default);

        Assert.False(cache.TryGet("ENG-1", UserA, out _));
        Assert.True(cache.TryGet("ENG-2", UserA, out _));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, typeof(ForbiddenException))]
    [InlineData(HttpStatusCode.NotFound, typeof(NotFoundException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(HttpRequestException))]
    public async Task Failures_are_mapped_and_not_cached(HttpStatusCode status, Type expected)
    {
        var (client, handler, _, _) = Create(() => new HttpResponseMessage(status));

        await Assert.ThrowsAsync(expected, () => client.GetAccessAsync("ENG-1", default));
        await Assert.ThrowsAsync(expected, () => client.GetAccessAsync("ENG-1", default));

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task RequireAsync_throws_when_the_capability_is_missing()
    {
        var (client, _, _, _) = Create(Ok);

        await client.RequireAsync("ENG-1", Capabilities.Edit, default);
        await Assert.ThrowsAsync<ForbiddenException>(() => client.RequireAsync("ENG-1", Capabilities.Submit, default));
    }
}
