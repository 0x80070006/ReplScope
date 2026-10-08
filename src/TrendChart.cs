using System.Drawing.Drawing2D;

namespace ReplScope;

/// <summary>Histogramme empilé OK / alerte / échec par collecte (les N dernières).</summary>
public sealed class TrendChart : Control
{
    private IReadOnlyList<ScanSample> _samples = Array.Empty<ScanSample>();
    private int _hover = -1;
    private readonly ToolTip _tip = new();

    public TrendChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Color.White;
        MinimumSize = new Size(200, 120);
    }

    public void SetSamples(IReadOnlyList<ScanSample> s) { _samples = s; Invalidate(); }

    private Rectangle Plot => new(44, 12, Math.Max(10, Width - 44 - 12), Math.Max(10, Height - 12 - 30));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var r = Plot;
        using var axis = new Pen(Color.Silver);
        g.DrawRectangle(axis, r);

        if (_samples.Count == 0)
        {
            TextRenderer.DrawText(g, "Aucune collecte enregistrée pour le moment.", Font, r, SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        int max = Math.Max(1, _samples.Max(s => s.Links));
        for (int i = 0; i <= 4; i++)
        {
            int y = r.Bottom - r.Height * i / 4;
            if (i > 0 && i < 4) { using var gl = new Pen(Color.Gainsboro) { DashStyle = DashStyle.Dot }; g.DrawLine(gl, r.Left, y, r.Right, y); }
            TextRenderer.DrawText(g, (max * i / 4).ToString(), Font, new Rectangle(0, y - 8, 40, 16), SystemColors.GrayText, TextFormatFlags.Right);
        }
        float bw = (float)r.Width / _samples.Count;
        float w = Math.Max(1f, Math.Min(bw - 1f, 28f));
        using var okB = new SolidBrush(Color.FromArgb(0, 150, 60));
        using var wB = new SolidBrush(Color.FromArgb(240, 170, 0));
        using var fB = new SolidBrush(Color.FromArgb(200, 30, 30));
        for (int i = 0; i < _samples.Count; i++)
        {
            var s = _samples[i];
            float x = r.Left + bw * i + (bw - w) / 2;
            float hOk = (float)r.Height * s.Ok / max, hW = (float)r.Height * s.Warn / max, hF = (float)r.Height * s.Fail / max;
            float y = r.Bottom;
            g.FillRectangle(okB, x, y - hOk, w, hOk); y -= hOk;
            g.FillRectangle(wB, x, y - hW, w, hW); y -= hW;
            g.FillRectangle(fB, x, y - hF, w, hF);
            if (i == _hover) g.DrawRectangle(Pens.Black, x, r.Top, w, r.Height);
        }
        var first = _samples[0].At; var last = _samples[^1].At;
        TextRenderer.DrawText(g, first.ToString("dd/MM HH:mm"), Font, new Point(r.Left, r.Bottom + 4), SystemColors.GrayText);
        TextRenderer.DrawText(g, last.ToString("dd/MM HH:mm"), Font, new Rectangle(r.Right - 120, r.Bottom + 4, 120, 16), SystemColors.GrayText, TextFormatFlags.Right);
        int lx = r.Left + r.Width / 2 - 120;
        foreach (var (b, t) in new[] { (okB, "OK"), (wB, "Alerte"), (fB, "Échec") })
        {
            g.FillRectangle(b, lx, r.Bottom + 8, 10, 10);
            TextRenderer.DrawText(g, t, Font, new Point(lx + 13, r.Bottom + 4), ForeColor);
            lx += 80;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_samples.Count == 0) return;
        var r = Plot;
        int i = (int)((e.X - r.Left) / ((float)r.Width / _samples.Count));
        if (i < 0 || i >= _samples.Count || !r.Contains(e.Location)) i = -1;
        if (i == _hover) return;
        _hover = i; Invalidate();
        if (i < 0) { _tip.Hide(this); return; }
        var s = _samples[i];
        _tip.Show($"{s.At:g}\nOK : {s.Ok}   Alerte : {s.Warn}   Échec : {s.Fail}\nDC injoignables : {s.DcsFailed}/{s.Dcs}\nÂge max : {Metrics.FormatAge(TimeSpan.FromMinutes(s.MaxAgeMin))}\nCollecte : {s.DurationSec:0.0} s", this, e.X + 12, e.Y + 12);
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; _tip.Hide(this); Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) _tip.Dispose(); base.Dispose(disposing); }
}
