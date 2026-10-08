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
    private AdCredential? _cred;            // compte alternatif, en mémoire uniquement
    private bool _askedCred;                // une seule proposition automatique par session
    private readonly ToolStripStatusLabel _account = new("") { BorderSides = ToolStripStatusLabelBorderSides.Left };

    // Métriques et historique
    private readonly HistoryStore _hist = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly List<(Panel Box, Label Value, Label Sub)> _tiles = new();
    private readonly ListView _byDc = new() { Dock = DockStyle.Bottom, Height = 170, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, BackColor = Color.White };
    private readonly TrendChart _chart = new() { Dock = DockStyle.Fill };
    private readonly ListView _histList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, BackColor = Color.White };
    private readonly ComboBox _histKind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly Label _histCount = new() { AutoSize = true };
    private DateTime _lastScanEnd; private double _lastScanSec;
    private const int HistoryRows = 5000;

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
        statusStrip.Items.AddRange(new ToolStripItem[] { _status, _count, _bar, _account });
        UpdateAccount();

        _hist.Load();
        BuildTabs();
        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 400 };
        right.Panel1.Controls.Add(_tabs);
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
        _tree.AfterSelect += (_, _) => { FillList(); RefreshMetrics(); FillHistory(); };
        _filter.TextChanged += (_, _) => { FillList(); FillHistory(); };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) _cts?.Cancel(); if (e.KeyCode == Keys.F5) StartScan(); };
        FormClosing += (_, _) => { _cts?.Cancel(); _uiTimer.Stop(); _autoTimer.Stop(); };
        Shown += (_, _) => { RefreshMetrics(); FillHistory(); StartScan(); };
    }

    // ---------- Onglets : liens / métriques / historique ----------
    private void BuildTabs()
    {
        var pLinks = new TabPage("Liens de réplication") { UseVisualStyleBackColor = true };
        pLinks.Controls.Add(_list);

        // Métriques
        var pMetrics = new TabPage("Métriques") { UseVisualStyleBackColor = true };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6), WrapContents = true };
        foreach (var cap in new[] { "Contrôleurs de domaine", "Liens de réplication", "Liens sains", "Liens en alerte", "Liens en échec",
                                    "Échecs consécutifs", "Âge max du dernier succès", "Âge moyen", "Temps de réponse moyen", "Dernière collecte", "DC le plus dégradé" })
            flow.Controls.Add(MakeTile(cap));
        foreach (var (t, w, a) in new[] { ("DC", 190, HorizontalAlignment.Left), ("Site", 100, HorizontalAlignment.Left), ("État", 70, HorizontalAlignment.Left),
                                          ("Liens", 55, HorizontalAlignment.Right), ("OK", 45, HorizontalAlignment.Right), ("Alertes", 60, HorizontalAlignment.Right),
                                          ("Échecs", 60, HorizontalAlignment.Right), ("Âge max", 90, HorizontalAlignment.Right), ("Réponse", 80, HorizontalAlignment.Right) })
            _byDc.Columns.Add(new ColumnHeader { Text = t, Width = w, TextAlign = a });
        _byDc.SmallImageList = _icons;
        var chartBox = new GroupBox { Text = "Tendance par collecte (liens OK / alerte / échec) :", Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 6) };
        chartBox.Controls.Add(_chart);
        pMetrics.Controls.Add(chartBox);
        pMetrics.Controls.Add(_byDc);
        pMetrics.Controls.Add(flow);
        pMetrics.AutoScroll = true;

        // Historique
        var pHist = new TabPage("Historique") { UseVisualStyleBackColor = true };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(4, 4, 0, 0) };
        _histKind.Items.Add("Tous les événements");
        _histKind.Items.AddRange(EventKind.All);
        _histKind.SelectedIndex = 0;
        _histKind.SelectedIndexChanged += (_, _) => FillHistory();
        var clear = new Button { Text = "Effacer l'historique", AutoSize = true };
        clear.Click += (_, _) => ClearHistory();
        bar.Controls.AddRange(new Control[] { new Label { Text = "Type :", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _histKind, clear,
                                              new Label { Text = "  ", AutoSize = true }, _histCount });
        _histCount.Margin = new Padding(8, 6, 0, 0);
        foreach (var (t, w, a) in new[] { ("Heure", 130, HorizontalAlignment.Left), ("Type", 140, HorizontalAlignment.Left), ("DC cible", 170, HorizontalAlignment.Left),
                                          ("Partition", 220, HorizontalAlignment.Left), ("Source", 170, HorizontalAlignment.Left), ("Code", 80, HorizontalAlignment.Left),
                                          ("Échecs", 55, HorizontalAlignment.Right), ("Détail", 320, HorizontalAlignment.Left) })
            _histList.Columns.Add(new ColumnHeader { Text = t, Width = w, TextAlign = a });
        _histList.SmallImageList = _icons;
        pHist.Controls.Add(_histList);
        pHist.Controls.Add(bar);

        _tabs.TabPages.AddRange(new[] { pLinks, pMetrics, pHist });
        _tabs.SelectedIndexChanged += (_, _) => { RefreshMetrics(); FillHistory(); };
    }

    private Panel MakeTile(string caption)
    {
        var box = new Panel { Width = 190, Height = 66, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, Margin = new Padding(3) };
        var cap = new Label { Text = caption, Left = 6, Top = 4, Width = 176, Height = 16, ForeColor = SystemColors.GrayText, AutoEllipsis = true };
        var val = new Label { Text = "—", Left = 6, Top = 20, Width = 176, Height = 28, Font = new Font("Segoe UI", 15f, FontStyle.Bold), ForeColor = Accent, AutoEllipsis = true };
        var sub = new Label { Text = "", Left = 6, Top = 48, Width = 176, Height = 15, ForeColor = SystemColors.GrayText, AutoEllipsis = true };
        box.Controls.AddRange(new Control[] { cap, val, sub });
        _tiles.Add((box, val, sub));
        return box;
    }

    private static readonly Color Green = Color.FromArgb(0, 130, 50), Amber = Color.FromArgb(200, 130, 0), Red = Color.FromArgb(190, 25, 25);

    private void SetTile(int i, string value, string sub = "", Color? color = null)
    {
        var (_, v, s) = _tiles[i];
        v.Text = value; v.ForeColor = color ?? Accent; s.Text = sub;
    }

    private (List<DcState> Dcs, string? Partition) ScopeDcs()
    {
        var tag = _tree.SelectedNode?.Tag as string ?? "F";
        IEnumerable<DcState> dcs = _dcs.Values;
        string? part = null;
        if (tag.StartsWith("S:")) dcs = dcs.Where(d => d.Site == tag[2..]);
        else if (tag.StartsWith("D:")) dcs = dcs.Where(d => string.Equals(d.Name, tag[2..], StringComparison.OrdinalIgnoreCase));
        else if (tag.StartsWith("P:")) { var p = tag[2..].Split('|', 2); dcs = dcs.Where(d => string.Equals(d.Name, p[0], StringComparison.OrdinalIgnoreCase)); part = p[1]; }
        return (dcs.ToList(), part);
    }

    private void RefreshMetrics()
    {
        var (dcs, part) = ScopeDcs();
        var m = Metrics.Compute(dcs, part, _th);
        var scope = _tree.SelectedNode?.Text ?? "forêt";
        SetTile(0, m.Dcs.ToString(), $"{m.DcsOk} OK · {m.DcsWarn} alerte · {m.DcsFailed} échec", m.DcsFailed > 0 ? Red : m.DcsWarn > 0 ? Amber : null);
        SetTile(1, m.Links.ToString(), $"{m.Partitions} partition(s) — {scope}");
        SetTile(2, m.Links == 0 ? "—" : $"{m.HealthyPercent:0.#} %", $"{m.LinksOk} sur {m.Links}",
            m.Links == 0 ? null : m.LinksFail > 0 ? Red : m.LinksWarn > 0 ? Amber : Green);
        SetTile(3, m.LinksWarn.ToString(), $"> {Metrics.FormatAge(_th.Warn)} sans succès", m.LinksWarn > 0 ? Amber : Green);
        SetTile(4, m.LinksFail.ToString(), $"erreur ou > {Metrics.FormatAge(_th.Fail)}", m.LinksFail > 0 ? Red : Green);
        SetTile(5, m.ConsecutiveFailures.ToString(), "somme sur les liens", m.ConsecutiveFailures > 0 ? Red : Green);
        SetTile(6, Metrics.FormatAge(m.MaxAge), "plus ancien dernier succès", m.MaxAge > _th.Fail ? Red : m.MaxAge > _th.Warn ? Amber : null);
        SetTile(7, Metrics.FormatAge(m.AvgAge), "moyenne des liens");
        SetTile(8, m.AvgResponse is { } a ? $"{a.TotalSeconds:0.0} s" : "—", m.MaxResponse is { } x ? $"max {x.TotalSeconds:0.0} s" : "");
        SetTile(9, _lastScanEnd == default ? "—" : $"{_lastScanSec:0.0} s", _lastScanEnd == default ? "" : _lastScanEnd.ToString("g"));
        SetTile(10, m.WorstDc is null ? "Aucun" : m.WorstDc.Split('.')[0], m.WorstDc ?? "", m.WorstDc is null ? Green : Red);

        _byDc.BeginUpdate();
        _byDc.Items.Clear();
        foreach (var d in dcs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var dm = Metrics.Compute(new[] { d }, part, _th);
            var it = new ListViewItem(d.Name, (int)d.Health);
            it.SubItems.AddRange(new[] { d.Site, d.Error is not null ? "Injoignable" : d.Health.ToString() == "Ok" ? "OK" : d.Health switch { Health.Warning => "Alerte", Health.Failed => "Échec", Health.Running => "En cours", _ => "En attente" },
                dm.Links.ToString(), dm.LinksOk.ToString(), dm.LinksWarn.ToString(), dm.LinksFail.ToString(),
                Metrics.FormatAge(dm.MaxAge), d.Duration > TimeSpan.Zero ? $"{d.Duration.TotalSeconds:0.0} s" : "—" });
            if (d.Health == Health.Failed) it.ForeColor = Color.Firebrick; else if (d.Health == Health.Warning) it.ForeColor = Color.DarkGoldenrod;
            _byDc.Items.Add(it);
        }
        _byDc.EndUpdate();
        _chart.SetSamples(_hist.Data.Samples.Count > 60 ? _hist.Data.Samples.GetRange(_hist.Data.Samples.Count - 60, 60) : _hist.Data.Samples.ToList());
    }

    private void FillHistory()
    {
        var (dcs, part) = ScopeDcs();
        var names = dcs.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kind = _histKind.SelectedIndex > 0 ? (string)_histKind.SelectedItem! : null;
        var f = _filter.Text.Trim();
        var rows = _hist.Data.Events
            .Where(e => (names.Count == 0 || names.Contains(e.Dc)) && (part is null || e.Partition.Length == 0 || e.Partition == part)
                        && (kind is null || e.Kind == kind)
                        && (f.Length == 0 || (e.Dc + " " + e.Partition + " " + e.Source + " " + e.Detail).Contains(f, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.At).ToList();
        _histList.BeginUpdate();
        _histList.Items.Clear();
        foreach (var e in rows.Take(HistoryRows))
        {
            var it = new ListViewItem(e.At.ToString("dd/MM/yyyy HH:mm:ss"), (int)EventKind.Level(e.Kind));
            it.SubItems.AddRange(new[] { e.Kind, e.Dc, e.Partition, e.Source, e.ErrorCode == 0 ? "0" : "0x" + e.ErrorCode.ToString("X8"), e.Failures.ToString(), e.Detail });
            var lvl = EventKind.Level(e.Kind);
            if (lvl == Health.Failed) it.ForeColor = Color.Firebrick; else if (lvl == Health.Warning) it.ForeColor = Color.DarkGoldenrod;
            _histList.Items.Add(it);
        }
        _histList.EndUpdate();
        _histCount.Text = $"{rows.Count} événement(s)" + (rows.Count > HistoryRows ? $" (les {HistoryRows} plus récents affichés)" : "")
                          + $" — conservés {HistoryStore.Retention.TotalDays:0} jours";
    }

    private void ClearHistory()
    {
        if (MessageBox.Show(this, "Effacer tout l'historique enregistré sur ce poste ?", "ReplScope", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _hist.Clear(); RefreshMetrics(); FillHistory();
    }

    private void ExportHistory()
    {
        using var sfd = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"ReplScope_historique_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        var sb = new StringBuilder("Heure;Type;DC;Site;Partition;Source;Code;Echecs;Detail\r\n");
        foreach (var e in _hist.Data.Events.OrderByDescending(x => x.At))
            sb.Append(string.Join(';', new[] { e.At.ToString("s"), e.Kind, e.Dc, e.Site, e.Partition, e.Source,
                e.ErrorCode.ToString(CultureInfo.InvariantCulture), e.Failures.ToString(CultureInfo.InvariantCulture), e.Detail }.Select(CsvExport.Cell))).Append("\r\n");
        try { CsvExport.Write(sfd.FileName, sb.ToString()); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ---------- UI construction ----------
    private MenuStrip BuildMenu()
    {
        var ms = new MenuStrip();
        var file = new ToolStripMenuItem("&Fichier");
        file.DropDownItems.Add("&Exporter les liens en CSV…", null, (_, _) => ExportCsv());
        file.DropDownItems.Add("Exporter l'&historique en CSV…", null, (_, _) => ExportHistory());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("&Quitter", null, (_, _) => Close());
        var action = new ToolStripMenuItem("&Action");
        action.DropDownItems.Add("&Actualiser\tF5", null, (_, _) => StartScan());
        action.DropDownItems.Add("A&rrêter\tÉchap", null, (_, _) => _cts?.Cancel());
        action.DropDownItems.Add("&Seuils d'alerte…", null, (_, _) => EditThresholds());
        action.DropDownItems.Add(new ToolStripSeparator());
        action.DropDownItems.Add("Se &connecter en tant que…", null, (_, _) => { if (AskCredentials(ReplicationService.MachineDomain(), null)) StartScan(); });
        action.DropDownItems.Add("Utiliser le compte de la session &Windows", null, (_, _) => { _cred = null; UpdateAccount(); StartScan(); });
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
        _ = ReplicationService.ScanAsync(f.Length == 0 ? null : f, _cred, _th, _progress, _cts.Token);
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
                _dirty = true;
                if (done.Error is null && !done.Cancelled && _dcs.Count > 0)
                {
                    _lastScanEnd = DateTime.Now; _lastScanSec = (_lastScanEnd - _scanStart).TotalSeconds;
                    var ev = _hist.Record(_dcs.Values.ToList(), _th, _lastScanEnd, _lastScanSec);
                    _hist.Save();
                    var bad = ev.Count(x => EventKind.Level(x.Kind) == Health.Failed);
                    Log($"Historique : {ev.Count} événement(s) enregistré(s)" + (bad > 0 ? $" dont {bad} échec(s)" : ""), bad > 0 ? Color.Firebrick : Color.Black);
                    FillHistory();
                }
                // Proposition automatique : une fois pour la session Windows, puis à chaque refus d'un compte saisi.
                if (done.NeedsCredentials && (!_askedCred || _cred is not null))
                {
                    _askedCred = true;
                    var why = _cred is null
                        ? $"La session Windows ({Environment.UserDomainName}\\{Environment.UserName}) n'est pas un compte du domaine."
                        : $"Le compte {_cred.User} a été refusé.";
                    _cred = null; UpdateAccount();
                    BeginInvoke(() => { if (AskCredentials(done.MachineDomain, why)) StartScan(); });
                }
                break;
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
        RefreshMetrics();
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

    private string SiteOf(string dc) => CsvExport.SiteOf(_dcs, dc);

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
        var sb = new StringBuilder(CsvExport.Header).Append("\r\n");
        foreach (var (d, l) in Scoped()) sb.Append(CsvExport.Row(_dcs, d, l, _th)).Append("\r\n");
        try { CsvExport.Write(sfd.FileName, sb.ToString()); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void UpdateAccount()
    {
        _account.Text = _cred is null ? $"Compte : {Environment.UserDomainName}\\{Environment.UserName}" : $"Compte : {_cred.User}";
        _account.ToolTipText = _cred is null ? "Authentification Kerberos de la session Windows" : "Compte alternatif (mémoire uniquement, jamais enregistré)";
    }

    /// <summary>Demande un compte du domaine. Le mot de passe reste en mémoire et n'est jamais écrit ni journalisé.</summary>
    private bool AskCredentials(string? domain, string? reason)
    {
        using var dlg = new Form
        {
            Text = "Se connecter en tant que", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, ClientSize = new Size(400, reason is null ? 150 : 190), Font = Font
        };
        int y = 15;
        if (reason is not null)
        {
            dlg.Controls.Add(new Label { Text = reason + "\nIndiquez un compte du domaine pour interroger les contrôleurs.", Left = 15, Top = y, Width = 370, Height = 40 });
            y += 45;
        }
        var user = new TextBox { Left = 150, Top = y, Width = 230, Text = domain is null ? "" : domain.Split('.')[0].ToUpperInvariant() + "\\" };
        var pwd = new TextBox { Left = 150, Top = y + 32, Width = 230, UseSystemPasswordChar = true, MaxLength = 256 };
        var ok = new Button { Text = "OK", Left = 220, Top = y + 72, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Annuler", Left = 305, Top = y + 72, DialogResult = DialogResult.Cancel };
        dlg.Controls.AddRange(new Control[] {
            new Label { Text = "Utilisateur (DOMAINE\\nom) :", Left = 15, Top = y + 3, AutoSize = true }, user,
            new Label { Text = "Mot de passe :", Left = 15, Top = y + 35, AutoSize = true }, pwd, ok, cancel });
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        dlg.Shown += (_, _) => { user.Focus(); user.SelectionStart = user.TextLength; };
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        var u = user.Text.Trim();
        // DOMAINE\nom ou nom@domaine.fqdn, caractères limités (pas d'injection dans le contexte LDAP)
        if (u.Length is 0 or > 256 || u.IndexOfAny(new[] { '"', '/', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>' }) >= 0
            || (!u.Contains('\\') && !u.Contains('@')) || u.EndsWith('\\') || pwd.TextLength == 0)
        {
            MessageBox.Show(this, "Saisissez un compte au format DOMAINE\\nom ou nom@domaine, et son mot de passe.", "ReplScope", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        _cred = new AdCredential(u, pwd.Text);
        pwd.Clear();
        UpdateAccount();
        Log($"Compte alternatif : {u}", Color.Black);
        return true;
    }

    private void About()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        MessageBox.Show(this, $"ReplScope {v?.ToString(3)}\nMoniteur de réplication Active Directory.\nLecture seule — Kerberos du compte courant, ou compte alternatif conservé en mémoire uniquement.\nWindows Server 2022 / 2025 / 2026.",
            "À propos de ReplScope", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
