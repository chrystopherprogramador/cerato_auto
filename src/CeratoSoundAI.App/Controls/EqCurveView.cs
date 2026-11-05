using CeratoSoundAI.Application;
using CeratoSoundAI.Domain;
using Microsoft.Maui.Graphics;

namespace CeratoSoundAI.App.Controls;

/// <summary>Vector EQ curve (Microsoft.Maui.Graphics). Bands come from the engine; nothing is drawn that the hardware doesn't have.</summary>
public sealed class EqCurveDrawable : IDrawable
{
    public IReadOnlyList<double> Centers { get; set; } = Array.Empty<double>();
    public IReadOnlyList<double> Gains { get; set; } = Array.Empty<double>();
    public IReadOnlyList<double>? Compare { get; set; }   // A/B or "before"
    public double MinDb { get; set; } = -12;
    public double MaxDb { get; set; } = 12;
    public int Selected { get; set; } = -1;
    public bool Interactive { get; set; }
    public bool Dim { get; set; }   // control unavailable / simulated

    private static readonly Color Grid = Color.FromArgb("#2E4560");
    private static readonly Color Accent = Color.FromArgb("#48C9DE");
    private static readonly Color Text2 = Color.FromArgb("#A9BACD");

    private const float PadL = 44, PadR = 16, PadT = 14, PadB = 30;

    public float XFor(RectF r, double hz)
    {
        if (Centers.Count == 0) return r.Left;
        double lo = Math.Log10(Centers[0] / 1.6), hi = Math.Log10(Centers[^1] * 1.6);
        return (float)(r.Left + PadL + (Math.Log10(hz) - lo) / (hi - lo) * (r.Width - PadL - PadR));
    }
    public float YFor(RectF r, double db) =>
        (float)(r.Top + PadT + (MaxDb - db) / (MaxDb - MinDb) * (r.Height - PadT - PadB));
    public double DbFor(RectF r, float y) =>
        MaxDb - (y - r.Top - PadT) / (r.Height - PadT - PadB) * (MaxDb - MinDb);

    public int NearestBand(RectF r, float x)
    {
        int best = -1; float d = float.MaxValue;
        for (int i = 0; i < Centers.Count; i++) { var dx = Math.Abs(XFor(r, Centers[i]) - x); if (dx < d) { d = dx; best = i; } }
        return best;
    }

    public void Draw(ICanvas c, RectF r)
    {
        c.FillColor = Color.FromArgb("#1B3042"); c.FillRoundedRectangle(r, 16);
        c.FontColor = Text2; c.FontSize = 11;

        if (Centers.Count == 0 || MaxDb <= MinDb)
        {
            c.FontSize = 15;
            c.DrawString("Sem bandas de equalização controláveis", r, HorizontalAlignment.Center, VerticalAlignment.Center);
            return;
        }

        // dB grid
        double step = (MaxDb - MinDb) > 20 ? 6 : 3;
        for (double db = Math.Ceiling(MinDb / step) * step; db <= MaxDb; db += step)
        {
            var y = YFor(r, db);
            c.StrokeColor = db == 0 ? Color.FromArgb("#4F6D8C") : Grid; c.StrokeSize = db == 0 ? 1.5f : 1;
            c.DrawLine(r.Left + PadL, y, r.Right - PadR, y);
            c.DrawString($"{db:+0;-0;0}", r.Left + 2, y - 8, PadL - 8, 16, HorizontalAlignment.Right, VerticalAlignment.Center);
        }
        // band verticals + labels
        for (int i = 0; i < Centers.Count; i++)
        {
            var x = XFor(r, Centers[i]);
            c.StrokeColor = i == Selected ? Accent.WithAlpha(0.5f) : Grid.WithAlpha(0.6f); c.StrokeSize = 1;
            c.DrawLine(x, r.Top + PadT, x, r.Bottom - PadB);
            c.FontColor = i == Selected ? Accent : Text2;
            c.DrawString(FrequencyMath.Format(Centers[i]), x - 24, r.Bottom - PadB + 4, 48, 20, HorizontalAlignment.Center, VerticalAlignment.Top);
        }

        if (Compare != null && Compare.Count == Centers.Count) DrawCurve(c, r, Compare, Text2.WithAlpha(0.7f), 2, false, true);
        DrawCurve(c, r, Gains, Dim ? Text2 : Accent, 4, true, false);
    }

    private void DrawCurve(ICanvas c, RectF r, IReadOnlyList<double> g, Color color, float w, bool fill, bool dashed)
    {
        int n = Math.Min(g.Count, Centers.Count);
        if (n == 0) return;
        var pts = new PointF[n];
        for (int i = 0; i < n; i++) pts[i] = new PointF(XFor(r, Centers[i]), YFor(r, Math.Clamp(g[i], MinDb, MaxDb)));

        var path = new PathF(pts[0]);
        for (int i = 0; i < n - 1; i++) // Catmull-Rom → cubic Bezier (visual smoothing only)
        {
            var p0 = pts[Math.Max(i - 1, 0)]; var p1 = pts[i]; var p2 = pts[i + 1]; var p3 = pts[Math.Min(i + 2, n - 1)];
            path.CurveTo(new PointF(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6),
                         new PointF(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6), p2);
        }
        if (fill)
        {
            var fp = new PathF(path);
            float zero = YFor(r, 0);
            fp.LineTo(pts[^1].X, zero); fp.LineTo(pts[0].X, zero); fp.Close();
            c.SaveState();
            c.SetFillPaint(new LinearGradientPaint(new[] { new PaintGradientStop(0, color.WithAlpha(0.35f)), new PaintGradientStop(1, color.WithAlpha(0.02f)) }, new Point(0, 0), new Point(0, 1)), r);
            c.FillPath(fp);
            c.RestoreState();
        }
        c.StrokeColor = color; c.StrokeSize = w; c.StrokeLineCap = LineCap.Round;
        if (dashed) c.StrokeDashPattern = new float[] { 6, 5 };
        c.DrawPath(path);
        c.StrokeDashPattern = null;

        if (Interactive && fill)
            for (int i = 0; i < n; i++)
            {
                c.FillColor = i == Selected ? Colors.White : Accent; c.FillCircle(pts[i], i == Selected ? 11 : 7);
                c.StrokeColor = Color.FromArgb("#101722"); c.StrokeSize = 2; c.DrawCircle(pts[i], i == Selected ? 11 : 7);
            }
    }
}

public sealed class EqCurveView : GraphicsView
{
    public EqCurveDrawable Curve { get; } = new();
    public event EventHandler<(int Band, double Db)>? BandDragged;
    public event EventHandler<int>? BandSelected;

    public EqCurveView()
    {
        Drawable = Curve;
        StartInteraction += (_, e) => Handle(e.Touches);
        DragInteraction += (_, e) => Handle(e.Touches);
    }

    private void Handle(PointF[] touches)
    {
        if (!Curve.Interactive || touches.Length == 0 || Curve.Centers.Count == 0) return;
        var r = new RectF(0, 0, (float)Width, (float)Height);
        var t = touches[0];
        int band = Curve.NearestBand(r, t.X);
        if (band < 0) return;
        if (band != Curve.Selected) { Curve.Selected = band; BandSelected?.Invoke(this, band); }
        double db = Math.Round(Curve.DbFor(r, t.Y) * 2) / 2.0; // 0.5 dB steps
        BandDragged?.Invoke(this, (band, db));
    }

    public void Update(IReadOnlyList<double> centers, IReadOnlyList<double> gains, double min, double max, int selected = -1, bool interactive = false, bool dim = false, IReadOnlyList<double>? compare = null)
    {
        Curve.Centers = centers; Curve.Gains = gains; Curve.MinDb = min; Curve.MaxDb = max;
        Curve.Selected = selected; Curve.Interactive = interactive; Curve.Dim = dim; Curve.Compare = compare;
        Invalidate();
    }
}
