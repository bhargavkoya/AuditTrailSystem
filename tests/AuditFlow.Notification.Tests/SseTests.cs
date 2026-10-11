using System.Text;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Notification.Api.Data;
using AuditFlow.Notification.Api.Sse;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AuditFlow.Notification.Tests;

public class SseTests
{
    private const string Eng = "ENG-1";

    private static readonly string[] AuditorCaps = [Capabilities.View, Capabilities.AttachView, Capabilities.CommentView];
    private static readonly string[] ViewerCaps = [Capabilities.View];

    private sealed class FakeLog : IEventLogReader
    {
        public List<SseFrame> Rows { get; } = [];
        public long Max => Rows.Count == 0 ? 0 : Rows.Max(r => r.Seq);

        public Task<IReadOnlyList<SseFrame>> ReadAfterAsync(string? engagementId, long afterSeq, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SseFrame>>(Rows
                .Where(r => r.Seq > afterSeq && (engagementId is null || r.EngagementId == engagementId))
                .OrderBy(r => r.Seq).Take(take).ToList());

        public Task<long> MaxSeqAsync(CancellationToken ct) => Task.FromResult(Max);
    }

    private sealed class Rig
    {
        public Rig(SseOptions? options = null)
        {
            Options = Microsoft.Extensions.Options.Options.Create(options ?? new SseOptions { HeartbeatSeconds = 1 });
            Hub = new EventHub(Options);
            Session = new SseSession(Hub, Log, Options, TimeProvider.System);
        }

        public IOptions<SseOptions> Options { get; }
        public FakeLog Log { get; } = new();
        public EventHub Hub { get; }
        public SseSession Session { get; }
    }

    private static SseFrame Frame(long seq, string type, string engagement = Eng, string payload = "{}")
        => new(seq, type, engagement, $$"""{"eventType":"{{type}}","engagementId":"{{engagement}}","payload":{{payload}}}""");

    /// <summary>Runs a session writing to a memory stream until <paramref name="until"/> is satisfied, then cancels it.</summary>
    private static async Task<string> RunUntilAsync(
        Rig rig, string[] caps, long? lastEventId, Func<string, bool> until, DateTimeOffset? expiresAt = null, Func<Task>? whileRunning = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new GrowableStream();
        var run = rig.Session.RunAsync(Eng, caps, lastEventId, expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), buffer, cts.Token);

        if (whileRunning is not null)
        {
            await WaitAsync(() => buffer.Text.Contains("event: ready") || buffer.Text.Contains("event: resync") || lastEventId is not null, cts.Token);
            await whileRunning();
        }

        await WaitAsync(() => until(buffer.Text) || run.IsCompleted, cts.Token);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        return buffer.Text;
    }

    private static async Task WaitAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }

    private sealed class GrowableStream : Stream
    {
        private readonly MemoryStream inner = new();
        private readonly object gate = new();

        public string Text { get { lock (gate) return Encoding.UTF8.GetString(inner.ToArray()); } }
        public override void Write(byte[] buffer, int offset, int count) { lock (gate) inner.Write(buffer, offset, count); }
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // ---- Filtering (Viewers never see attachment or comment activity) ---------------------------------------------

    [Theory]
    [InlineData(EventTypes.AttachmentAdded, false)]
    [InlineData(EventTypes.ReviewerCommentAdded, false)]
    [InlineData(EventTypes.SectionUpdated, true)]
    [InlineData(EventTypes.ValidationSummaryChanged, true)]
    [InlineData(EventTypes.EngagementStatusChanged, true)]
    [InlineData(EventTypes.ReviewApproved, true)]
    public void A_viewer_only_receives_events_that_are_not_attachment_or_comment_activity(string type, bool allowed)
        => Assert.Equal(allowed, SseFilter.Allows(type, ViewerCaps));

    [Theory]
    [InlineData(EventTypes.AttachmentAdded)]
    [InlineData(EventTypes.ReviewerCommentAdded)]
    public void Participants_with_the_capabilities_receive_everything(string type)
        => Assert.True(SseFilter.Allows(type, AuditorCaps));

    [Fact]
    public void Every_event_type_is_decided_without_throwing()
        => Assert.All(EventTypes.All, t => SseFilter.Allows(t, ViewerCaps));

    // ---- Hub ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_hub_delivers_only_to_subscribers_of_that_engagement()
    {
        var rig = new Rig();
        using var a = rig.Hub.Subscribe("ENG-1");
        using var b = rig.Hub.Subscribe("ENG-2");

        rig.Hub.Publish(Frame(1, EventTypes.SectionUpdated, "ENG-1"));

        Assert.True(a.Reader.TryRead(out var got));
        Assert.Equal(1, got!.Seq);
        Assert.False(b.Reader.TryRead(out _));
        await Task.CompletedTask;
    }

    [Fact]
    public void A_slow_subscriber_overflows_instead_of_growing_without_bound()
    {
        var rig = new Rig(new SseOptions { ChannelCapacity = 3 });
        using var slow = rig.Hub.Subscribe(Eng);

        for (var i = 1; i <= 5; i++) rig.Hub.Publish(Frame(i, EventTypes.SectionUpdated));

        Assert.True(slow.Overflowed);
    }

    [Fact]
    public void Disposing_a_subscription_removes_it()
    {
        var rig = new Rig();
        var subscription = rig.Hub.Subscribe(Eng);
        Assert.Equal(1, rig.Hub.SubscriberCount);

        subscription.Dispose();

        Assert.Equal(0, rig.Hub.SubscriberCount);
    }

    // ---- Session: ready, live, filtering, no values ---------------------------------------------------------------

    [Fact]
    public async Task A_new_connection_is_told_it_is_ready_with_the_current_position()
    {
        var rig = new Rig();
        rig.Log.Rows.Add(Frame(7, EventTypes.SectionUpdated));

        var text = await RunUntilAsync(rig, AuditorCaps, null, t => t.Contains("event: ready"));

        Assert.Contains("id: 7\nevent: ready", text);
    }

    [Fact]
    public async Task Live_events_are_written_as_sse_frames_with_their_sequence_as_the_id()
    {
        var rig = new Rig();

        var text = await RunUntilAsync(rig, AuditorCaps, null, t => t.Contains("event: SectionUpdated"),
            whileRunning: () => { rig.Hub.Publish(Frame(11, EventTypes.SectionUpdated, payload: """{"sectionKey":"section-2-1","changedFields":["bookValue"],"version":"AAA="}""")); return Task.CompletedTask; });

        Assert.Contains("id: 11\nevent: SectionUpdated\ndata: {", text);
        Assert.EndsWith("\n\n", text);
    }

    [Fact]
    public async Task A_viewer_never_receives_attachment_or_comment_events_but_gets_the_events_around_them()
    {
        var rig = new Rig();

        var text = await RunUntilAsync(rig, ViewerCaps, null, t => t.Contains("event: ValidationSummaryChanged"), whileRunning: () =>
        {
            rig.Hub.Publish(Frame(1, EventTypes.AttachmentAdded, payload: """{"fileName":"secret.pdf"}"""));
            rig.Hub.Publish(Frame(2, EventTypes.ReviewerCommentAdded));
            rig.Hub.Publish(Frame(3, EventTypes.ValidationSummaryChanged));
            return Task.CompletedTask;
        });

        Assert.DoesNotContain("AttachmentAdded", text);
        Assert.DoesNotContain("secret.pdf", text);
        Assert.DoesNotContain("ReviewerCommentAdded", text);
        Assert.Contains("event: ValidationSummaryChanged", text);
    }

    [Fact]
    public void The_frame_data_is_a_single_line_even_if_the_stored_envelope_had_line_breaks()
    {
        var text = SseSession.Format(new SseFrame(1, EventTypes.SectionUpdated, Eng, "{\n  \"a\": 1\n}"));

        Assert.Equal(1, text.Split('\n').Count(l => l.StartsWith("data:")));
        Assert.DoesNotContain("\n  ", text);
    }

    // ---- Session: replay and resync -------------------------------------------------------------------------------

    [Fact]
    public async Task Reconnecting_replays_only_what_was_missed_for_this_engagement_in_order()
    {
        var rig = new Rig();
        rig.Log.Rows.AddRange([
            Frame(1, EventTypes.SectionUpdated), Frame(2, EventTypes.SectionUpdated, "ENG-OTHER"),
            Frame(3, EventTypes.ConfigurationChanged), Frame(4, EventTypes.ValidationSummaryChanged)]);

        var text = await RunUntilAsync(rig, AuditorCaps, 1, t => t.Contains("id: 4\n"));

        Assert.DoesNotContain("id: 1\n", text);
        Assert.DoesNotContain("ENG-OTHER", text);
        Assert.True(text.IndexOf("id: 3\n", StringComparison.Ordinal) < text.IndexOf("id: 4\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replay_applies_the_same_role_filter()
    {
        var rig = new Rig();
        rig.Log.Rows.AddRange([Frame(2, EventTypes.AttachmentAdded), Frame(3, EventTypes.SectionUpdated)]);

        var text = await RunUntilAsync(rig, ViewerCaps, 1, t => t.Contains("id: 3\n"));

        Assert.DoesNotContain("AttachmentAdded", text);
    }

    [Fact]
    public async Task A_gap_larger_than_the_replay_cap_becomes_a_resync_instead_of_a_huge_replay()
    {
        var rig = new Rig(new SseOptions { ReplayCap = 3, HeartbeatSeconds = 1 });
        for (var i = 1; i <= 10; i++) rig.Log.Rows.Add(Frame(i, EventTypes.SectionUpdated));

        var text = await RunUntilAsync(rig, AuditorCaps, 0, t => t.Contains("event: resync"));

        Assert.Contains("id: 10\nevent: resync", text);
        Assert.DoesNotContain("event: SectionUpdated", text);
    }

    [Fact]
    public async Task A_client_ahead_of_the_log_is_told_to_resync()
    {
        var rig = new Rig();
        rig.Log.Rows.Add(Frame(2, EventTypes.SectionUpdated));

        var text = await RunUntilAsync(rig, AuditorCaps, 999, t => t.Contains("event: resync"));

        Assert.Contains("event: resync", text);
    }

    [Fact]
    public async Task An_event_seen_during_replay_is_not_repeated_by_the_live_feed()
    {
        var rig = new Rig();
        rig.Log.Rows.Add(Frame(5, EventTypes.SectionUpdated));

        var text = await RunUntilAsync(rig, AuditorCaps, 4, t => t.Contains("id: 6\n"), whileRunning: () =>
        {
            rig.Hub.Publish(Frame(5, EventTypes.SectionUpdated)); // tailer delivering the same row again
            rig.Hub.Publish(Frame(6, EventTypes.SectionUpdated));
            return Task.CompletedTask;
        });

        Assert.Equal(1, Occurrences(text, "id: 5\n"));
    }

    // ---- Session: heartbeat, overflow, reauth ---------------------------------------------------------------------

    [Fact]
    public async Task An_idle_stream_sends_heartbeats()
    {
        var rig = new Rig(new SseOptions { HeartbeatSeconds = 0 }); // clamps to 50 ms

        var text = await RunUntilAsync(rig, AuditorCaps, null, t => t.Contains(": heartbeat"));

        Assert.Contains(": heartbeat", text);
    }

    [Fact]
    public async Task A_client_that_falls_behind_is_told_to_resync_and_the_stream_ends()
    {
        var rig = new Rig(new SseOptions { ChannelCapacity = 1, HeartbeatSeconds = 1 });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = new GrowableStream();
        // Fill the channel before the session starts reading so it overflows deterministically.
        var run = rig.Session.RunAsync(Eng, AuditorCaps, null, DateTimeOffset.UtcNow.AddHours(1), output, cts.Token);
        await WaitAsync(() => output.Text.Contains("event: ready"), cts.Token);
        for (var i = 1; i <= 50; i++) rig.Hub.Publish(Frame(i, EventTypes.SectionUpdated));

        await WaitAsync(() => run.IsCompleted, cts.Token);

        Assert.Contains("event: resync", output.Text);
    }

    [Fact]
    public async Task An_expired_token_ends_the_stream_with_reauth()
    {
        var rig = new Rig();

        var text = await RunUntilAsync(rig, AuditorCaps, null, t => t.Contains("event: reauth"), expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(-1));

        Assert.Contains("event: reauth", text);
    }

    // ---- Tailer ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_tailer_publishes_new_committed_rows_once_and_catches_late_committed_lower_sequences()
    {
        var rig = new Rig();
        rig.Log.Rows.Add(Frame(1, EventTypes.SectionUpdated));
        var tailer = new EventLogTailer(rig.Log, rig.Hub, rig.Options, NullLogger<EventLogTailer>.Instance);
        using var subscription = rig.Hub.Subscribe(Eng);

        Assert.Equal(1, await tailer.PollAsync(default));
        Assert.Equal(0, await tailer.PollAsync(default)); // nothing new, nothing repeated
        rig.Log.Rows.Add(Frame(3, EventTypes.SectionUpdated));
        Assert.Equal(1, await tailer.PollAsync(default));
        rig.Log.Rows.Add(Frame(2, EventTypes.SectionUpdated)); // committed after 3 although allocated before it
        Assert.Equal(1, await tailer.PollAsync(default));

        var seqs = new List<long>();
        while (subscription.Reader.TryRead(out var f)) seqs.Add(f.Seq);
        Assert.Equal([1L, 3L, 2L], seqs);
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
