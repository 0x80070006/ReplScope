using System.Text;

namespace ReplScope;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0) return Cli(args);

        using var mutex = new Mutex(true, @"Local\ReplScope.SingleInstance", out bool first);
        if (!first) return 0;
        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static void Fatal(Exception? ex) =>
        MessageBox.Show(ex?.Message ?? "Erreur inconnue", "ReplScope", MessageBoxButtons.OK, MessageBoxIcon.Error);

    /// <summary>
    /// Mode sans interface (tâche planifiée, supervision) :
    ///   ReplScope.exe --export C:\chemin\etat.csv [--forest pessac.lan]
    /// Compte de la session uniquement (aucun mot de passe en ligne de commande).
    /// Code de retour : 0 = OK, 1 = alerte, 2 = échec de réplication, 3 = erreur de collecte, 4 = arguments invalides.
    /// </summary>
    private static int Cli(string[] args)
    {
        string? output = null, forest = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--export" when i + 1 < args.Length: output = args[++i]; break;
                case "--forest" when i + 1 < args.Length: forest = args[++i]; break;
                default: return 4;
            }
        }
        if (output is null || (forest is not null && !ReplicationService.IsValidForestName(forest))) return 4;
        try { output = Path.GetFullPath(output); } catch { return 4; }

        var th = Thresholds.Default;
        var dcs = new Dictionary<string, DcState>(StringComparer.OrdinalIgnoreCase);
        string? error = null;
        var progress = new SyncProgress(e =>
        {
            switch (e)
            {
                case DcsDiscovered d: foreach (var dc in d.Dcs) dcs[dc.Name] = dc; break;
                case DcFinished f: dcs[f.Dc] = f.State; break;
                case ScanDone done: error = done.Error; break;
            }
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        ReplicationService.ScanAsync(forest, null, th, progress, cts.Token).GetAwaiter().GetResult();

        var sb = new StringBuilder(CsvExport.Header).Append("\r\n");
        var worst = Health.Ok;
        foreach (var d in dcs.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (d.Error is not null) { sb.Append(CsvExport.ErrorRow(d)).Append("\r\n"); worst = Health.Failed; continue; }
            foreach (var l in d.Links)
            {
                sb.Append(CsvExport.Row(dcs, d, l, th)).Append("\r\n");
                var h = ReplicationService.Evaluate(l, th); if (h > worst) worst = h;
            }
        }
        if (error is not null) sb.Append(CsvExport.Cell("#ERREUR")).Append(';').Append(CsvExport.Cell(error)).Append("\r\n");
        try { CsvExport.Write(output, sb.ToString()); } catch { return 3; }
        return error is not null || dcs.Count == 0 ? 3 : worst switch { Health.Failed => 2, Health.Warning => 1, _ => 0 };
    }

    /// <summary>IProgress synchrone et sérialisé (pas de contexte de synchronisation en mode console).</summary>
    private sealed class SyncProgress(Action<ScanEvent> handler) : IProgress<ScanEvent>
    {
        private readonly object _gate = new();
        public void Report(ScanEvent value) { lock (_gate) handler(value); }
    }
}
