using System.DirectoryServices.ActiveDirectory;
using System.Text.RegularExpressions;

namespace ReplScope;

/// <summary>Collecte en lecture seule des voisins de réplication, DC par DC.</summary>
public static partial class ReplicationService
{
    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$",
        RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex HostName();

    public static bool IsValidForestName(string s)
    {
        try { return HostName().IsMatch(s); } catch (RegexMatchTimeoutException) { return false; }
    }

    private const int MaxParallel = 8;
    private static readonly TimeSpan DcTimeout = TimeSpan.FromSeconds(30);

    public static async Task ScanAsync(string? forestName, Thresholds th, IProgress<ScanEvent> progress, CancellationToken ct)
    {
        try
        {
            var (forest, dcs) = await Task.Run(() => Discover(forestName), ct);
            progress.Report(new ForestFound(DateTime.Now, forest));
            progress.Report(new DcsDiscovered(DateTime.Now, dcs));

            using var gate = new SemaphoreSlim(MaxParallel);
            var tasks = dcs.Select(async dc =>
            {
                try { await gate.WaitAsync(ct); } catch (OperationCanceledException) { return; }
                try
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report(new DcStarted(DateTime.Now, dc.Name));
                    await QueryDcAsync(dc, th, ct);
                    progress.Report(new DcFinished(DateTime.Now, dc.Name, dc));
                }
                catch (OperationCanceledException) { }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
            progress.Report(new ScanDone(DateTime.Now, ct.IsCancellationRequested, null));
        }
        catch (OperationCanceledException) { progress.Report(new ScanDone(DateTime.Now, true, null)); }
        catch (Exception ex) { progress.Report(new ScanDone(DateTime.Now, false, ex.Message)); }
    }

    private static (string, List<DcState>) Discover(string? forestName)
    {
        var ctx = string.IsNullOrWhiteSpace(forestName)
            ? new DirectoryContext(DirectoryContextType.Forest)
            : new DirectoryContext(DirectoryContextType.Forest, forestName.Trim());
        using var forest = Forest.GetForest(ctx);
        var list = new List<DcState>();
        foreach (Domain d in forest.Domains)
        {
            using (d)
                foreach (DomainController dc in d.DomainControllers)
                    using (dc)
                        list.Add(new DcState { Name = dc.Name, Site = dc.SiteName ?? "(inconnu)", Domain = d.Name });
        }
        return (forest.Name, list);
    }

    private static async Task QueryDcAsync(DcState dc, Thresholds th, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var work = Task.Run(() => Fetch(dc.Name));
        var done = await Task.WhenAny(work, Task.Delay(DcTimeout, ct));
        ct.ThrowIfCancellationRequested();
        sw.Stop();
        dc.Duration = sw.Elapsed;
        if (done != work) { dc.Health = Health.Failed; dc.Error = $"Délai dépassé ({DcTimeout.TotalSeconds:0} s)"; return; }
        try
        {
            dc.Links = work.Result;
            dc.Health = Evaluate(dc.Links, th);
        }
        catch (Exception ex)
        {
            dc.Health = Health.Failed;
            dc.Error = (ex.InnerException ?? ex).Message;
        }
    }

    private static List<ReplLink> Fetch(string dcName)
    {
        var ctx = new DirectoryContext(DirectoryContextType.DirectoryServer, dcName);
        using var dc = DomainController.GetDomainController(ctx);
        var res = new List<ReplLink>();
        foreach (ReplicationNeighbor n in dc.GetAllReplicationNeighbors())
            res.Add(new ReplLink(n.PartitionName ?? "", n.SourceServer ?? "", "", n.TransportType.ToString(),
                n.LastSuccessfulSync, n.LastAttemptedSync, n.ConsecutiveFailureCount, n.LastSyncResult, n.LastSyncMessage ?? ""));
        return res;
    }

    public static Health Evaluate(ReplLink l, Thresholds th)
    {
        if (l.ErrorCode != 0 || l.Failures > 0) return Health.Failed;
        var age = l.Age;
        if (age is null) return Health.Warning;
        if (age > th.Fail) return Health.Failed;
        return age > th.Warn ? Health.Warning : Health.Ok;
    }

    public static Health Evaluate(IEnumerable<ReplLink> links, Thresholds th)
    {
        var worst = Health.Ok;
        foreach (var l in links) { var h = Evaluate(l, th); if (h > worst) worst = h; }
        return worst;
    }
}
