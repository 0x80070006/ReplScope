namespace ReplScope;

/// <summary>Indicateurs agrégés sur un périmètre (forêt, site, DC ou partition).</summary>
public sealed record Metrics(
    int Dcs, int DcsOk, int DcsWarn, int DcsFailed, int Partitions,
    int Links, int LinksOk, int LinksWarn, int LinksFail,
    int ConsecutiveFailures, TimeSpan? MaxAge, TimeSpan? AvgAge,
    TimeSpan? AvgResponse, TimeSpan? MaxResponse, string? WorstDc)
{
    public double HealthyPercent => Links == 0 ? 0 : 100.0 * LinksOk / Links;

    public static Metrics Compute(IReadOnlyCollection<DcState> dcs, string? partition, Thresholds th)
    {
        int ok = 0, warn = 0, fail = 0, lok = 0, lwarn = 0, lfail = 0, links = 0, consec = 0;
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ages = new List<TimeSpan>();
        var resp = new List<TimeSpan>();
        string? worstDc = null; int worstScore = 0;

        foreach (var d in dcs)
        {
            switch (d.Health)
            {
                case Health.Ok: ok++; break;
                case Health.Warning: warn++; break;
                case Health.Failed: fail++; break;
            }
            if (d.Duration > TimeSpan.Zero) resp.Add(d.Duration);
            int score = d.Error is not null ? 1000 : 0;
            foreach (var l in d.Links)
            {
                if (partition is not null && !string.Equals(l.Partition, partition, StringComparison.OrdinalIgnoreCase)) continue;
                links++; parts.Add(l.Partition);
                consec += l.Failures; score += l.Failures;
                if (l.Age is { } a) ages.Add(a);
                switch (ReplicationService.Evaluate(l, th))
                {
                    case Health.Ok: lok++; break;
                    case Health.Warning: lwarn++; score++; break;
                    case Health.Failed: lfail++; score += 10; break;
                }
            }
            if (score > worstScore) { worstScore = score; worstDc = d.Name; }
        }
        return new Metrics(dcs.Count, ok, warn, fail, parts.Count, links, lok, lwarn, lfail, consec,
            ages.Count == 0 ? null : ages.Max(),
            ages.Count == 0 ? null : TimeSpan.FromTicks((long)ages.Average(x => x.Ticks)),
            resp.Count == 0 ? null : TimeSpan.FromTicks((long)resp.Average(x => x.Ticks)),
            resp.Count == 0 ? null : resp.Max(), worstDc);
    }

    public static string FormatAge(TimeSpan? a) => a is not { } t ? "—"
        : t.TotalDays >= 1 ? $"{(int)t.TotalDays} j {t.Hours} h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} m" : $"{(int)t.TotalSeconds} s";
}
