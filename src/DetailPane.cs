using System.DirectoryServices;

namespace ReplScope;

public sealed record ReplObject(DateTime? When, string Operation, string Class, string Name, long Usn, string Dn);

/// <summary>Lit (lecture seule) les objets dont le uSNChanged est dans une plage, sur le DC source : ce sont ceux qui ont été répliqués.</summary>
public static class ChangeDetailService
{
    public const int MaxObjects = 500;
    private static readonly string[] Props = { "distinguishedName", "objectClass", "whenChanged", "whenCreated", "isDeleted", "uSNChanged", "name" };

    public static Task<(List<ReplObject> Items, bool Truncated)> LoadAsync(string sourceDc, string partition, long from, long to, AdCredential? cred, CancellationToken ct) =>
        Task.Run(() => Load(sourceDc, partition, from, to, cred, ct), ct);

    private static (List<ReplObject>, bool) Load(string sourceDc, string partition, long from, long to, AdCredential? cred, CancellationToken ct)
    {
        if (!ReplicationService.IsValidForestName(sourceDc)) throw new ArgumentException("Nom de DC invalide.");
        if (partition.Length is 0 or > 1024 || partition.Any(char.IsControl)) throw new ArgumentException("Partition invalide.");
        if (from < 0 || to < from) return (new List<ReplObject>(), false);

        var path = $"LDAP://{sourceDc}/{partition.Replace("/", "\\/")}";
        using var root = cred is null ? new DirectoryEntry(path) : new DirectoryEntry(path, cred.User, cred.Password);
        // Le filtre n'est construit qu'à partir de deux entiers : pas d'injection LDAP possible.
        using var s = new DirectorySearcher(root, $"(&(uSNChanged>={from})(uSNChanged<={to}))", Props, SearchScope.Subtree)
        {
            PageSize = 250,
            Tombstone = true,                                  // inclut les objets supprimés
            ReferralChasing = ReferralChasingOption.None,      // ne descend pas dans les autres partitions
            ServerTimeLimit = TimeSpan.FromSeconds(15),
            ClientTimeout = TimeSpan.FromSeconds(25)
        };
        var list = new List<ReplObject>();
        bool truncated = false;
        using var results = s.FindAll();
        foreach (SearchResult r in results)
        {
            ct.ThrowIfCancellationRequested();
            if (list.Count >= MaxObjects) { truncated = true; break; }
            var created = Get<DateTime?>(r, "whenCreated"); var changed = Get<DateTime?>(r, "whenChanged");
            var deleted = Get<bool?>(r, "isDeleted") == true;
            var op = deleted ? "Supprimé" : created is { } c && changed is { } m && (m - c).Duration() < TimeSpan.FromSeconds(3) ? "Créé" : "Modifié";
            string cls = "";
            if (r.Properties["objectClass"] is { Count: > 0 } oc) cls = oc[oc.Count - 1]?.ToString() ?? "";
            var dn = Get<string>(r, "distinguishedName") ?? r.Path;
            list.Add(new ReplObject(changed?.ToLocalTime(), op, cls, Get<string>(r, "name") ?? "", Get<long?>(r, "uSNChanged") ?? 0, dn));
        }
        list.Sort((a, b) => b.Usn.CompareTo(a.Usn));
        return (list, truncated);
    }

    private static T? Get<T>(SearchResult r, string name)
    {
        var col = r.Properties[name];
        if (col.Count == 0 || col[0] is null) return default;
        try { return (T)col[0]; } catch { return default; }
    }
}

public sealed class BufferedListView : ListView
{
    public BufferedListView() { DoubleBuffered = true; }
}

/// <summary>Volet « détail de ce qui est répliqué » : titre + liste des objets.</summary>
public sealed class DetailPane : UserControl
{
    private readonly Label _head = new() { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4, 4, 4, 0), AutoEllipsis = true };
    private readonly BufferedListView _lv = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, BackColor = Color.White };
    private readonly Func<AdCredential?> _cred;
    private CancellationTokenSource? _cts;
    private string _sig = "";

    public DetailPane(Func<AdCredential?> cred)
    {
        _cred = cred;
        Dock = DockStyle.Fill;
        foreach (var (t, w) in new[] { ("Heure modif.", 130), ("Opération", 75), ("Classe", 110), ("Nom", 190), ("USN", 80), ("Nom distinctif (DN)", 420) })
            _lv.Columns.Add(new ColumnHeader { Text = t, Width = w, TextAlign = t == "USN" ? HorizontalAlignment.Right : HorizontalAlignment.Left });
        Controls.Add(_lv);
        Controls.Add(_head);
        Clear("Sélectionnez un élément pour voir le détail de ce qui a été répliqué.");
    }

    public void Clear(string message)
    {
        _cts?.Cancel(); _sig = "";
        _lv.Items.Clear();
        _head.Text = message;
    }

    public async void ShowRange(string title, string sourceDc, string partition, long from, long to)
    {
        var sig = $"{title}|{sourceDc}|{partition}|{from}|{to}";
        if (sig == _sig) return;                       // même sélection : inutile de relire l'annuaire
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _sig = sig;
        _lv.Items.Clear();
        _head.Text = title + "\nChargement…";
        try
        {
            var (items, truncated) = await ChangeDetailService.LoadAsync(sourceDc, partition, from, to, _cred(), cts.Token);
            if (cts.IsCancellationRequested) return;
            _lv.BeginUpdate();
            foreach (var o in items)
            {
                var it = new ListViewItem(o.When?.ToString("G") ?? "—");
                it.SubItems.AddRange(new[] { o.Operation, o.Class, o.Name, o.Usn.ToString("N0"), o.Dn });
                if (o.Operation == "Supprimé") it.ForeColor = Color.Firebrick;
                else if (o.Operation == "Créé") it.ForeColor = Color.FromArgb(0, 120, 40);
                _lv.Items.Add(it);
            }
            _lv.EndUpdate();
            _head.Text = title + $"\n{items.Count} objet(s)" + (truncated ? $" — limité aux {ChangeDetailService.MaxObjects} plus récents" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            _sig = "";
            _head.Text = title + "\nDétail indisponible : " + ex.Message;
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) { _cts?.Cancel(); _cts?.Dispose(); } base.Dispose(disposing); }
}
