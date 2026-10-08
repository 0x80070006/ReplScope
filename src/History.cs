using System.Text.Json;

namespace ReplScope;

public static class EventKind
{
    public const string Success = "Réplication réussie";
    public const string Failure = "Échec";
    public const string Recovered = "Rétabli";
    public const string Warning = "Alerte";
    public const string DcDown = "DC injoignable";
    public const string DcUp = "DC rétabli";

    public static readonly string[] All = { Success, Failure, Recovered, Warning, DcDown, DcUp };

    public static Health Level(string kind) => kind switch
    {
        Failure or DcDown => Health.Failed,
        Warning => Health.Warning,
        _ => Health.Ok
    };
}

public sealed record HistoryEvent(DateTime At, string Kind, string Dc, string Site, string Partition, string Source,
                                  int ErrorCode, int Failures, string Detail);

/// <summary>Une collecte complète : sert au graphique de tendance.</summary>
public sealed record ScanSample(DateTime At, int Dcs, int DcsFailed, int Links, int Ok, int Warn, int Fail,
                                double MaxAgeMin, double DurationSec);

public sealed record LinkSnap(DateTime? LastSuccess, int Failures, int ErrorCode, Health Health);

public sealed class HistoryData
{
    public List<HistoryEvent> Events { get; set; } = new();
    public List<ScanSample> Samples { get; set; } = new();
    public Dictionary<string, LinkSnap> Last { get; set; } = new();
    public List<string> DcDown { get; set; } = new();
}

/// <summary>
/// Historique persistant (%LOCALAPPDATA%\ReplScope\history.json). Ne contient que des noms de DC,
/// de partitions et des codes d'erreur : aucun identifiant n'est jamais enregistré.
/// </summary>
public sealed class HistoryStore
{
    public const int MaxEvents = 20_000, MaxSamples = 2_000;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _path;
    public HistoryData Data { get; private set; } = new();

    public HistoryStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReplScope", "history.json");
    }

    public void Load()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length > 64 * 1024 * 1024) return;   // fichier absent ou anormalement gros
            Data = JsonSerializer.Deserialize<HistoryData>(File.ReadAllText(_path), Json) ?? new HistoryData();
        }
        catch { Data = new HistoryData(); }                           // fichier corrompu : on repart à zéro
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
            File.Move(tmp, _path, overwrite: true);                   // écriture atomique
        }
        catch { /* l'historique est un confort : une erreur d'écriture ne doit jamais bloquer la supervision */ }
    }

    public void Clear() { Data = new HistoryData(); Save(); }

    private static string Key(string dc, ReplLink l) => $"{dc}|{l.Partition}|{l.SourceDc}".ToLowerInvariant();

    /// <summary>Compare la collecte courante à la précédente, ajoute les événements et l'échantillon. Retourne les nouveaux événements.</summary>
    public List<HistoryEvent> Record(IReadOnlyCollection<DcState> dcs, Thresholds th, DateTime now, double durationSec)
    {
        var d = Data;
        var added = new List<HistoryEvent>();
        var first = d.Last.Count == 0 && d.Samples.Count == 0;
        var down = new HashSet<string>(d.DcDown, StringComparer.OrdinalIgnoreCase);

        foreach (var dc in dcs)
        {
            if (dc.Error is not null)
            {
                if (down.Add(dc.Name))
                    added.Add(new HistoryEvent(now, EventKind.DcDown, dc.Name, dc.Site, "", "", 0, 0, dc.Error));
                continue;                                              // état des liens conservé tant que le DC ne répond pas
            }
            if (dc.Health is Health.Pending or Health.Running) continue;
            if (down.Remove(dc.Name))
                added.Add(new HistoryEvent(now, EventKind.DcUp, dc.Name, dc.Site, "", "", 0, 0, "Le DC répond de nouveau"));

            foreach (var l in dc.Links)
            {
                var key = Key(dc.Name, l);
                var h = ReplicationService.Evaluate(l, th);
                d.Last.TryGetValue(key, out var prev);
                var ls = l.LastSuccess is { } t && t.Year > 1700 ? t : (DateTime?)null;

                HistoryEvent Ev(string kind, DateTime at, string detail) =>
                    new(at, kind, dc.Name, dc.Site, l.Partition, l.SourceDc, l.ErrorCode, l.Failures, detail);

                if (prev is not null && ls is { } cur && (prev.LastSuccess is not { } p || cur > p))
                    added.Add(Ev(EventKind.Success, cur.ToLocalTime(), "Synchronisation entrante réussie"));

                if (h == Health.Failed &&
                    (prev is null || prev.Health != Health.Failed || l.Failures > prev.Failures || l.ErrorCode != prev.ErrorCode))
                    added.Add(Ev(EventKind.Failure, now, l.ErrorCode != 0 ? $"0x{l.ErrorCode:X8} {l.Message}".Trim()
                        : $"Aucune réplication réussie depuis {Metrics.FormatAge(l.Age)}"));
                else if (h == Health.Warning && prev is not null && prev.Health == Health.Ok)
                    added.Add(Ev(EventKind.Warning, now, $"Dernier succès il y a {Metrics.FormatAge(l.Age)}"));
                else if (h == Health.Ok && prev is not null && prev.Health is Health.Failed or Health.Warning)
                    added.Add(Ev(EventKind.Recovered, now, "Retour à la normale"));

                d.Last[key] = new LinkSnap(ls, l.Failures, l.ErrorCode, h);
            }
        }
        // Liens disparus (topologie modifiée) : on les oublie pour que le snapshot ne grossisse pas indéfiniment.
        var live = dcs.Where(x => x.Error is null && x.Health >= Health.Ok)
                      .SelectMany(x => x.Links.Select(l => Key(x.Name, l))).ToHashSet();
        var downKeys = dcs.Where(x => x.Error is not null).Select(x => x.Name.ToLowerInvariant() + "|").ToList();
        foreach (var k in d.Last.Keys.ToList())
            if (!live.Contains(k) && !downKeys.Any(p => k.StartsWith(p))) d.Last.Remove(k);
        d.DcDown = down.ToList();

        var m = Metrics.Compute(dcs, null, th);
        d.Samples.Add(new ScanSample(now, m.Dcs, m.DcsFailed, m.Links, m.LinksOk, m.LinksWarn, m.LinksFail,
            m.MaxAge?.TotalMinutes ?? 0, durationSec));
        d.Events.AddRange(added);
        Prune(now);
        _ = first;
        return added;
    }

    private void Prune(DateTime now)
    {
        var limit = now - Retention;
        Data.Events.RemoveAll(e => e.At < limit);
        Data.Samples.RemoveAll(s => s.At < limit);
        if (Data.Events.Count > MaxEvents) Data.Events.RemoveRange(0, Data.Events.Count - MaxEvents);
        if (Data.Samples.Count > MaxSamples) Data.Samples.RemoveRange(0, Data.Samples.Count - MaxSamples);
    }
}
