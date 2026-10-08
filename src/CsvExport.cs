using System.Globalization;
using System.Text;

namespace ReplScope;

/// <summary>Export CSV commun à l'interface et au mode ligne de commande.</summary>
public static class CsvExport
{
    public const string Header = "DC;Partition;Source;SiteSource;DernierSucces;AgeMinutes;Echecs;Code;Message;Etat;USN";

    public static string SiteOf(IReadOnlyDictionary<string, DcState> dcs, string dc) =>
        dcs.TryGetValue(dc, out var d) ? d.Site
        : dcs.Values.FirstOrDefault(x => dc.StartsWith(x.Name.Split('.')[0] + ".", StringComparison.OrdinalIgnoreCase))?.Site ?? "—";

    public static string Row(IReadOnlyDictionary<string, DcState> dcs, DcState d, ReplLink l, Thresholds th) =>
        string.Join(';', new[] {
            d.Name, l.Partition, l.SourceDc, SiteOf(dcs, l.SourceDc), l.LastSuccess?.ToString("s") ?? "",
            l.Age?.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) ?? "", l.Failures.ToString(CultureInfo.InvariantCulture),
            l.ErrorCode.ToString(CultureInfo.InvariantCulture), l.Message, ReplicationService.Evaluate(l, th).ToString(), l.Usn.ToString(CultureInfo.InvariantCulture) }.Select(Cell));

    public static string ErrorRow(DcState d) =>
        string.Join(';', new[] { d.Name, "", "", "", "", "", "", "", d.Error ?? "", Health.Failed.ToString(), "" }.Select(Cell));

    public static void Write(string path, string content) => File.WriteAllText(path, content, new UTF8Encoding(true));

    /// <summary>Neutralise l'injection de formules (=, +, -, @, tab, CR) et échappe les séparateurs.</summary>
    public static string Cell(string s)
    {
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
