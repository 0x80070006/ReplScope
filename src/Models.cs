namespace ReplScope;

public enum Health { Pending, Running, Ok, Warning, Failed }

public sealed record ReplLink(
    string Partition, string SourceDc, string SourceSite, string Transport,
    DateTime? LastSuccess, DateTime? LastAttempt, int Failures, int ErrorCode, string Message)
{
    public TimeSpan? Age => LastSuccess is { } t && t.Year > 1700 ? DateTime.Now - t.ToLocalTime() : null;
}

public sealed class DcState
{
    public required string Name { get; init; }
    public string Site { get; init; } = "(inconnu)";
    public string Domain { get; init; } = "";
    public Health Health { get; set; } = Health.Pending;
    public List<ReplLink> Links { get; set; } = new();
    public TimeSpan Duration { get; set; }
    public string? Error { get; set; }
}

public abstract record ScanEvent(DateTime At);
public sealed record ForestFound(DateTime At, string Forest) : ScanEvent(At);
public sealed record DcsDiscovered(DateTime At, IReadOnlyList<DcState> Dcs) : ScanEvent(At);
public sealed record DcStarted(DateTime At, string Dc) : ScanEvent(At);
public sealed record DcFinished(DateTime At, string Dc, DcState State) : ScanEvent(At);
public sealed record ScanDone(DateTime At, bool Cancelled, string? Error) : ScanEvent(At);

public sealed record Thresholds(TimeSpan Warn, TimeSpan Fail)
{
    public static Thresholds Default => new(TimeSpan.FromHours(1), TimeSpan.FromHours(24));
}
