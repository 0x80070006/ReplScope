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

    /// <summary>Domaine DNS de la machine (vide si hors domaine). Ne nécessite aucun contexte de sécurité de domaine.</summary>
    public static string? MachineDomain()
    {
        try
        {
            var d = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
            return string.IsNullOrWhiteSpace(d) || !IsValidForestName(d) ? null : d;
        }
        catch { return null; }
    }

    private static DirectoryContext Ctx(DirectoryContextType type, string? name, AdCredential? cred) =>
        cred is null
            ? (name is null ? new DirectoryContext(type) : new DirectoryContext(type, name))
            : new DirectoryContext(type, name ?? throw new ArgumentNullException(nameof(name)), cred.User, cred.Password);

    public static async Task ScanAsync(string? forestName, AdCredential? cred, Thresholds th, IProgress<ScanEvent> progress, CancellationToken ct)
    {
        try
        {
            var (forest, dcs) = await Task.Run(() => Discover(forestName, cred), ct);
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
                    await QueryDcAsync(dc, cred, th, ct);
                    progress.Report(new DcFinished(DateTime.Now, dc.Name, dc));
                }
                catch (OperationCanceledException) { }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
            progress.Report(new ScanDone(DateTime.Now, ct.IsCancellationRequested, null));
        }
        catch (OperationCanceledException) { progress.Report(new ScanDone(DateTime.Now, true, null)); }
        catch (NoDomainContextException ex) { progress.Report(new ScanDone(DateTime.Now, false, ex.Message, NeedsCredentials: true, MachineDomain: ex.MachineDomain)); }
        catch (System.Security.Authentication.AuthenticationException ex) { progress.Report(new ScanDone(DateTime.Now, false, "Authentification refusée : " + ex.Message, NeedsCredentials: true, MachineDomain: MachineDomain())); }
        catch (UnauthorizedAccessException ex) { progress.Report(new ScanDone(DateTime.Now, false, "Accès refusé : " + ex.Message, NeedsCredentials: true, MachineDomain: MachineDomain())); }
        catch (Exception ex) { progress.Report(new ScanDone(DateTime.Now, false, (ex.InnerException ?? ex).Message)); }
    }

    private static (string, List<DcState>) Discover(string? forestName, AdCredential? cred)
    {
        var name = string.IsNullOrWhiteSpace(forestName) ? null : forestName.Trim();
        Forest forest;
        if (name is null && cred is null)
        {
            // Session courante : fonctionne seulement si le compte Windows est un compte du domaine.
            try { forest = Forest.GetCurrentForest(); }
            catch (ActiveDirectoryOperationException) { throw new NoDomainContextException(MachineDomain()); }
        }
        else
        {
            if (name is null)
            {
                // Compte alternatif sans forêt saisie : on part du domaine de la machine et on remonte à sa forêt.
                var md = MachineDomain() ?? throw new NoDomainContextException(null);
                using var dom = Domain.GetDomain(Ctx(DirectoryContextType.Domain, md, cred));
                using var f0 = dom.Forest;
                name = f0.Name;
            }
            forest = Forest.GetForest(Ctx(DirectoryContextType.Forest, name, cred));
        }
        using var _ = forest;
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

    private static async Task QueryDcAsync(DcState dc, AdCredential? cred, Thresholds th, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var work = Task.Run(() => Fetch(dc.Name, cred));
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

    private static List<ReplLink> Fetch(string dcName, AdCredential? cred)
    {
        using var dc = DomainController.GetDomainController(Ctx(DirectoryContextType.DirectoryServer, dcName, cred));
        // File de réplication : opération en cours et opérations en attente (nécessite des droits de lecture, sinon ignorée).
        var pending = new List<(string Partition, string Src, string Text)>();
        try
        {
            var info = dc.GetReplicationOperationInformation();
            if (info.CurrentOperation is { } cur)
                pending.Add((cur.PartitionName ?? "", ShortName(cur.SourceServer), "En cours : " + OpName(cur.OperationType)));
            foreach (ReplicationOperation op in info.PendingOperations!)
                pending.Add((op.PartitionName ?? "", ShortName(op.SourceServer), $"En file d'attente (n° {op.OperationNumber}) : {OpName(op.OperationType)}"));
        }
        catch { }

        var res = new List<ReplLink>();
        foreach (ReplicationNeighbor n in dc.GetAllReplicationNeighbors())
        {
            var part = n.PartitionName ?? ""; var src = n.SourceServer ?? "";
            var p = pending.FirstOrDefault(x => string.Equals(x.Partition, part, StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(x.Src, ShortName(src), StringComparison.OrdinalIgnoreCase));
            res.Add(new ReplLink(part, src, "", n.TransportType.ToString(),
                n.LastSuccessfulSync, n.LastAttemptedSync, n.ConsecutiveFailureCount, n.LastSyncResult, n.LastSyncMessage ?? "",
                n.UsnLastObjectChangeSynced, p.Text));
        }
        return res;
    }

    private static string ShortName(string? s) => (s ?? "").Split('.')[0];

    private static string OpName(ReplicationOperationType t) => t switch
    {
        ReplicationOperationType.Sync => "synchronisation",
        ReplicationOperationType.Add => "ajout de réplica",
        ReplicationOperationType.Delete => "suppression de réplica",
        ReplicationOperationType.Modify => "modification",
        ReplicationOperationType.UpdateReference => "mise à jour de référence",
        _ => t.ToString()
    };

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
