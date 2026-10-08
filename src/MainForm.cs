using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace ReplScope;

public sealed class MainForm : Form
{
    private static readonly Color Accent = Color.FromArgb(0, 84, 166);

    private readonly Dictionary<string, DcState> _dcs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Progress<ScanEvent> _progress;
    private CancellationTokenSource? _cts;
    private bool _dirty, _scanning;
    private string _forest = "";
    private DateTime _scanStart;
    private int _done;
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private Thresholds _th = Thresholds.Default;

    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, ShowLines = true, FullRowSelect = true };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, BackColor = Color.White };
    private readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.FixedSingle, MaxLength = 400000 };
    private readonly ToolStripButton _btnRefresh = new("Actualiser") { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
    private readonly ToolStripButton _btnStop = new("Arrêter") { Enabled = false };
    private readonly ToolStripButton _chkAuto = new("Mise à jour automatique") { CheckOnClick = true };
    private readonly ToolStripComboBox _interval = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly ToolStripTextBox _forestBox = new() { Width = 160, ToolTipText = "Nom de la forêt (vide = forêt courante)" };
    private readonly ToolStripTextBox _filter = new() { Width = 140, ToolTipText = "Filtrer (DC, partition, message)" };
    private readonly ToolStripStatusLabel _status = new("Prêt") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _count = new("");
    private readonly ToolStripProgressBar _bar = new() { Width = 140, Visible = false };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 150 };
    private readonly System.Windows.Forms.Timer _autoTimer = new() { Interval = 60_000 };
    private readonly ImageList _icons = new() { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };

    public MainForm()
    {
        Text = "ReplScope — Moniteur de réplication Active Directory";
        Font = new Font("Segoe UI", 9f);
        Size = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = SystemColors.Control;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        BuildIcons();
        _tree.ImageList = _icons;
        _tree.ImageIndex = _tree.SelectedImageIndex = (int)Health.Pending;
        foreach (var (t, w, a) in new[] {
            ("DC cible", 170, HorizontalAlignment.Left), ("Partition", 230, HorizontalAlignment.Left),
            ("Source", 170, HorizontalAlignment.Left), ("Site source", 100, HorizontalAlignment.Left),
            ("Dernier succès", 135, HorizontalAlignment.Left), ("Âge", 70, HorizontalAlignment.Right),
            ("Échecs", 55, HorizontalAlignment.Right), ("Code", 80, HorizontalAlignment.Left),
            ("Message", 300, HorizontalAlignment.Left) })
            _list.Columns.Add(new ColumnHeader { Text = t, Width = w, TextAlign = a });
        _list.SmallImageList = _icons;
        _list.ColumnClick += (_, e) => SortBy(e.Column);

        var menu = BuildMenu();
        var tool = BuildToolbar();
        var statusStrip = new StatusStrip { SizingGrip = true };
        statusStrip.Items.AddRange(new ToolStripItem[] { _status, _count, _bar });

        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 400 };
        right.Panel1.Controls.Add(_list);
        var logBox = new GroupBox { Text = "Journal :", Dock = DockStyle.Fill };
        logBox.Controls.Add(_log);
        _log.Dock = DockStyle.Fill;
        right.Panel2.Controls.Add(logBox);

        var main = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 300, FixedPanel = FixedPanel.Panel1 };
        var treeBox = new GroupBox { Text = "Serveurs surveillés :", Dock = DockStyle.Fill };
        treeBox.Controls.Add(_tree);
        main.Panel1.Controls.Add(treeBox);
        main.Panel2.Controls.Add(right);

        Controls.Add(main);
        Controls.Add(statusStrip);
        Controls.Add(tool);
        Controls.Add(menu);
        MainMenuStrip = menu;

        foreach (var m in new[] { "1 min", "5 min", "15 min", "30 min" }) _interval.Items.Add(m);
        _interval.SelectedIndex = 1;
        _interval.SelectedIndexChanged += (_, _) => SetInterval();
        SetInterval();

        _progress = new Progress<ScanEvent>(OnEvent);
        _btnRefresh.Click += (_, _) => StartScan();
        _btnStop.Click += (_, _) => _cts?.Cancel();
        _chkAuto.CheckedChanged += (_, _) => _autoTimer.Enabled = _chkAuto.Checked;
        _autoTimer.Tick += (_, _) => { if (!_scanning) StartScan(); };
        _uiTimer.Tick += (_, _) => { if (_dirty) { _dirty = false; Rebuild(); } UpdateStatus(); };
        _uiTimer.Start();
        _tree.AfterSelect += (_, _) => FillList();
        _filter.TextChanged += (_, _) => FillList();
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) _cts?.Cancel(); if (e.KeyCode == Keys.F5) StartScan(); };
        FormClosing += (_, _) => { _cts?.Cancel(); _uiTimer.Stop(); _autoTimer.Stop(); };
        Shown += (_, _) => StartScan();
    }

    // ---------- UI construction ----------
    private MenuStrip BuildMenu()
    {
        var ms = new MenuStrip();
        var file = new ToolStripMenuItem("&Fichier");
        file.DropDownItems.Add("&Exporter en CSV…", null, (_, _) => ExportCsv());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("&Quitter", null, (_, _) => Close());
        var action = new ToolStripMenuItem("&Action");
        action.DropDownItems.Add("&Actualiser\tF5", null, (_, _) => StartScan());
        action.DropDownItems.Add("A&rrêter\tÉchap", null, (_, _) => _cts?.Cancel());
        action.DropDownItems.Add("&Seuils d'alerte…", null, (_, _) => EditThresholds());
        var help = new ToolStripMenuItem("&?");
        help.DropDownItems.Add("À &propos de ReplScope", null, (_, _) => About());
        ms.Items.AddRange(new ToolStripItem[] { file, action, help });
        return ms;
    }

    private ToolStrip BuildToolbar()
    {
        var ts = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        ts.Items.AddRange(new ToolStripItem[] {
            _btnRefresh, _btnStop, new ToolStripSeparator(), _chkAuto, _interval,
            new ToolStripSeparator(), new ToolStripLabel("Forêt :"), _forestBox,
            new ToolStripSeparator(), new ToolStripLabel("Filtre :"), _filter });
        return ts;
    }

    private void BuildIcons()
    {
        var colors = new[] { Color.Gray, Accent, Color.FromArgb(0, 150, 60), Color.FromArgb(240, 170, 0), Color.FromArgb(200, 30, 30) };
        foreach (var c in colors)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var b = new SolidBrush(c);
                g.FillEllipse(b, 2, 2, 12, 12);
                g.DrawEllipse(Pens.White, 2, 2, 12, 12);
            }
            _icons.Images.Add(bmp);
        }
    }

    private void SetInterval()
    {
        int[] mins = { 1, 5, 15, 30 };
        _autoTimer.Interval = mins[Math.Max(0, _interval.SelectedIndex)] * 60_000;
    }

    // ---------- Scan ----------
    private void StartScan()
    {
        if (_scanning) return;
        var f = _forestBox.Text.Trim();
        if (f.Length > 0 && !ReplicationService.IsValidForestName(f))
        {
            MessageBox.Show(this, "Nom de forêt invalide.", "ReplScope", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _scanning = true;
        _cts = new CancellationTokenSource();
        _scanStart = DateTime.Now;
        _done = 0;
        _running.Clear();
        _btnRefresh.Enabled = false; _btnStop.Enabled = true; _forestBox.Enabled = false;
        _bar.Visible = true; _bar.Style = ProgressBarStyle.Marquee;
        Log("Début de la collecte", Color.Black);
        _ = ReplicationService.ScanAsync(f.Length == 0 ? null : f, _th, _progress, _cts.Token);
    }

    private void OnEvent(ScanEvent e)
    {
        switch (e)
        {
            case ForestFound ff:
                _forest = ff.Forest; Log($"Forêt découverte : {ff.Forest}", Color.Black); _dirty = true; break;
            case DcsDiscovered d:
                var names = d.Dcs.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var k in _dcs.Keys.Where(k => !names.Contains(k)).ToList()) _dcs.Remove(k);
                foreach (var dc in d.Dcs)
                    if (!_dcs.ContainsKey(dc.Name)) _dcs[dc.Name] = dc;
                _bar.Style = ProgressBarStyle.Continuous; _bar.Maximum = Math.Max(1, d.Dcs.Count); _bar.Value = 0;
                Log($"{d.Dcs.Count} contrôleur(s) de domaine trouvé(s)", Color.Black); _dirty = true; break;
            case DcStarted s:
                _running.Add(s.Dc);
                if (_dcs.TryGetValue(s.Dc, out var cur) && cur.Health == Health.Pending) cur.Health = Health.Running;
                _dirty = true; break;
            case DcFinished f:
                _running.Remove(f.Dc); _done++;
                _dcs[f.Dc] = f.State;
                if (_bar.Maximum >= _done) _bar.Value = _done;
                var ok = f.State.Health != Health.Failed;
                Log($"{f.Dc,-32} {(f.State.Error ?? $"{f.State.Links.Count} lien(s)")}  [{f.State.Duration.TotalSeconds:0.0} s]", ok ? Color.Black : Color.Firebrick);
                _dirty = true; break;
            case ScanDone done:
                _scanning = false; _cts?.Dispose(); _cts = null;
                _btnRefresh.Enabled = true; _btnStop.Enabled = false; _forestBox.Enabled = true; _bar.Visible = false;
                Log(done.Error is not null ? $"Erreur : {done.Error}" : done.Cancelled ? "Collecte annulée" : "Collecte terminée",
                    done.Error is not null ? Color.Firebrick : Color.Black);
                _status.Text = done.Error ?? $"Statut au : {DateTime.Now:g}";
                _dirty = true; break;
        }
    }

    private void UpdateStatus()
    {
        if (!_scanning) return;
        var run = _running.Count == 0 ? "" : " — interrogation : " + string.Join(", ", _running.Take(3).Select(s => s.Split('.')[0])) + (_running.Count > 3 ? "…" : "");
        _status.Text = $"Collecte en cours — {(DateTime.Now - _scanStart).TotalSeconds:0} s{run}";
        _count.Text = $"{_done}/{_dcs.Count}";
    }

    private void Log(string msg, Color c)
    {
        if (_log.TextLength > 300000) { _log.Select(0, 100000); _log.SelectedText = ""; }
        _log.SelectionStart = _log.TextLength; _log.SelectionColor = c;
        _log.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {msg}\n");
        _log.ScrollToCaret();
    }

    // ---------- Tree / List ----------
    private void Rebuild()
    {
        var sel = _tree.SelectedNode?.Tag as string;
        var open = new HashSet<string>();
        void Collect(TreeNodeCollection n) { foreach (TreeNode x in n) { if (x.IsExpanded && x.Tag is string t) open.Add(t); Collect(x.Nodes); } }
        Collect(_tree.Nodes);
        var firstBuild = _tree.Nodes.Count == 0;

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var worstAll = Health.Pending;
        var root = new TreeNode(string.IsNullOrEmpty(_forest) ? "Forêt" : _forest) { Tag = "F" };
        foreach (var site in _dcs.Values.GroupBy(d => d.Site).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sn = new TreeNode(site.Key) { Tag = "S:" + site.Key };
            var worst = Health.Pending;
            foreach (var dc in site.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                var dn = new TreeNode(dc.Name) { Tag = "D:" + dc.Name, ImageIndex = (int)dc.Health, SelectedImageIndex = (int)dc.Health };
                foreach (var p in dc.Links.Select(l => l.Partition).Distinct().OrderBy(x => x))
                {
                    var ph = ReplicationService.Evaluate(dc.Links.Where(l => l.Partition == p), _th);
                    dn.Nodes.Add(new TreeNode(p) { Tag = "P:" + dc.Name + "|" + p, ImageIndex = (int)ph, SelectedImageIndex = (int)ph });
                }
                sn.Nodes.Add(dn);
                if (dc.Health > worst) worst = dc.Health;
            }
            sn.ImageIndex = sn.SelectedImageIndex = (int)worst;
            if (worst > worstAll) worstAll = worst;
            root.Nodes.Add(sn);
        }
        root.ImageIndex = root.SelectedImageIndex = (int)worstAll;
        _tree.Nodes.Add(root);

        void Restore(TreeNodeCollection n)
        {
            foreach (TreeNode x in n)
            {
                if (firstBuild ? x.Level < 2 : open.Contains((string)x.Tag!)) x.Expand();
                if ((string?)x.Tag == sel) _tree.SelectedNode = x;
                Restore(x.Nodes);
            }
        }
        Restore(_tree.Nodes);
        if (_tree.SelectedNode is null) _tree.SelectedNode = root;
        _tree.EndUpdate();
        FillList();
    }

    private IEnumerable<(DcState Dc, ReplLink Link)> Scoped()
    {
        var tag = _tree.SelectedNode?.Tag as string ?? "F";
        IEnumerable<DcState> dcs = _dcs.Values;
        string? part = null;
        if (tag.StartsWith("S:")) dcs = dcs.Where(d => d.Site == tag[2..]);
        else if (tag.StartsWith("D:")) dcs = dcs.Where(d => string.Equals(d.Name, tag[2..], StringComparison.OrdinalIgnoreCase));
        else if (tag.StartsWith("P:")) { var p = tag[2..].Split('|', 2); dcs = dcs.Where(d => string.Equals(d.Name, p[0], StringComparison.OrdinalIgnoreCase)); part = p[1]; }
        var f = _filter.Text.Trim();
        foreach (var d in dcs)
            foreach (var l in d.Links)
            {
                if (part is not null && l.Partition != part) continue;
                if (f.Length > 0 && !(d.Name + " " + l.Partition + " " + l.SourceDc + " " + l.Message).Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
                yield return (d, l);
            }
    }

    private int _sortCol = -1; private bool _sortAsc = true;
    private void SortBy(int col)
    {
        _sortAsc = col != _sortCol || !_sortAsc; _sortCol = col;
        _list.ListViewItemSorter = new RowSorter(col, _sortAsc);
        _list.Sort();
    }

    private sealed class RowSorter(int col, bool asc) : System.Collections.IComparer
    {
        public int Compare(object? a, object? b)
        {
            var x = ((ListViewItem)a!).SubItems[col].Text; var y = ((ListViewItem)b!).SubItems[col].Text;
            int r = double.TryParse(x.Trim('h', 'm', 's', ' '), out var nx) && double.TryParse(y.Trim('h', 'm', 's', ' '), out var ny) && col is 6
                ? nx.CompareTo(ny) : string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
            return asc ? r : -r;
        }
    }

    private void FillList()
    {
        _list.BeginUpdate();
        var topKey = _list.TopItem?.Text;
        _list.Items.Clear();
        int n = 0;
        foreach (var (d, l) in Scoped())
        {
            var h = ReplicationService.Evaluate(l, _th);
            var it = new ListViewItem(d.Name, (int)h);
            it.SubItems.AddRange(new[] {
                l.Partition, l.SourceDc, SiteOf(l.SourceDc),
                l.LastSuccess is { } t && t.Year > 1700 ? t.ToLocalTime().ToString("g") : "—",
                FormatAge(l.Age), l.Failures.ToString(), l.ErrorCode == 0 ? "0" : "0x" + l.ErrorCode.ToString("X8"), l.Message });
            if (h == Health.Failed) it.ForeColor = Color.Firebrick; else if (h == Health.Warning) it.ForeColor = Color.DarkGoldenrod;
            _list.Items.Add(it); n++;
        }
        foreach (var d in _dcs.Values.Where(d => d.Error is not null && ScopeHas(d)))
        {
            var it = new ListViewItem(d.Name, (int)Health.Failed) { ForeColor = Color.Firebrick };
            it.SubItems.AddRange(new[] { "—", "—", "—", "—", "—", "—", "—", d.Error! });
            _list.Items.Add(it);
        }
        if (_sortCol >= 0) _list.Sort();
        _list.EndUpdate();
        _count.Text = $"{n} lien(s)";
    }

    private string SiteOf(string dc) =>
        _dcs.TryGetValue(dc, out var d) ? d.Site : _dcs.Values.FirstOrDefault(x => dc.StartsWith(x.Name.Split('.')[0] + ".", StringComparison.OrdinalIgnoreCase))?.Site ?? "—";

    private bool ScopeHas(DcState d)
    {
        var tag = _tree.SelectedNode?.Tag as string ?? "F";
        if (tag.StartsWith("S:")) return d.Site == tag[2..];
        if (tag.StartsWith("D:")) return string.Equals(d.Name, tag[2..], StringComparison.OrdinalIgnoreCase);
        return tag == "F";
    }

    private static string FormatAge(TimeSpan? a) => a is not { } t ? "—"
        : t.TotalDays >= 1 ? $"{(int)t.TotalDays} j {t.Hours} h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} m" : $"{(int)t.TotalSeconds} s";

    // ---------- Dialogs / export ----------
    private void EditThresholds()
    {
        using var dlg = new Form { Text = "Seuils d'alerte", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(320, 130), Font = Font };
        var warn = new NumericUpDown { Left = 200, Top = 15, Width = 90, Minimum = 1, Maximum = 720, Value = (decimal)_th.Warn.TotalMinutes };
        var fail = new NumericUpDown { Left = 200, Top = 45, Width = 90, Minimum = 2, Maximum = 10080, Value = (decimal)_th.Fail.TotalMinutes };
        var ok = new Button { Text = "OK", Left = 130, Top = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Annuler", Left = 215, Top = 90, DialogResult = DialogResult.Cancel };
        dlg.Controls.AddRange(new Control[] { new Label { Text = "Alerte après (minutes) :", Left = 15, Top = 18, AutoSize = true }, warn,
            new Label { Text = "Échec après (minutes) :", Left = 15, Top = 48, AutoSize = true }, fail, ok, cancel });
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (fail.Value <= warn.Value) { MessageBox.Show(this, "Le seuil d'échec doit être supérieur au seuil d'alerte."); return; }
        _th = new Thresholds(TimeSpan.FromMinutes((double)warn.Value), TimeSpan.FromMinutes((double)fail.Value));
        foreach (var d in _dcs.Values.Where(d => d.Error is null && d.Links.Count > 0)) d.Health = ReplicationService.Evaluate(d.Links, _th);
        _dirty = true;
    }

    private void ExportCsv()
    {
        using var sfd = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"ReplScope_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        var sb = new StringBuilder("DC;Partition;Source;SiteSource;DernierSucces;AgeMinutes;Echecs;Code;Message\r\n");
        foreach (var (d, l) in Scoped())
            sb.AppendJoin(';', new[] { d.Name, l.Partition, l.SourceDc, SiteOf(l.SourceDc), l.LastSuccess?.ToString("s") ?? "",
                l.Age?.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) ?? "", l.Failures.ToString(), l.ErrorCode.ToString(), l.Message }.Select(Csv)).AppendLine();
        try { File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // Neutralise l'injection de formules (=, +, -, @, tab, CR) et échappe les séparateurs.
    private static string Csv(string s)
    {
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private void About()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        MessageBox.Show(this, $"ReplScope {v?.ToString(3)}\nMoniteur de réplication Active Directory.\nLecture seule — authentification Kerberos du compte courant.\nWindows Server 2022 / 2025 / 2026.",
            "À propos de ReplScope", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
