namespace AuditFlow.BuildingBlocks.Correlation;

public static class CorrelationHeaders
{
    public const string CorrelationId = "X-Correlation-Id";
}

/// <summary>Exposes the correlation id of the current request/message.</summary>
public interface ICorrelationContext
{
    string CorrelationId { get; }
}

/// <summary>Ambient (AsyncLocal) correlation id so HTTP, outbox publishing and message handlers share one id.</summary>
public sealed class CorrelationContext : ICorrelationContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public string CorrelationId => Current.Value ?? string.Empty;

    public static IDisposable Begin(string correlationId)
    {
        var previous = Current.Value;
        Current.Value = correlationId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
