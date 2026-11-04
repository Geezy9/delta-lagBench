using System.Globalization;
using System.Text;

namespace Testerss;

/// <summary>
/// Renders a per-iteration progress chart (work done vs. time) as a
/// self-contained SVG, with an ideal-linear reference line and a
/// throughput annotation.
/// </summary>
internal static class SvgChartWriter
{
    private const int Width = 800, Height = 400;
    private const int MarginLeft = 70, MarginRight = 20, MarginTop = 40, MarginBottom = 50;

    public static void WriteProgressChart(
        string path,
        IReadOnlyList<(double Ms, long Done)> samples,
        long totalTasks,
        string title)
    {
        ArgumentOutOfRangeException.ThrowIfZero(samples.Count);
        var inv = CultureInfo.InvariantCulture;

        double maxMs = Math.Max(samples[^1].Ms, 1e-9);
        double plotW = Width - MarginLeft - MarginRight;
        double plotH = Height - MarginTop - MarginBottom;

        var points = new StringBuilder();
        foreach (var (ms, done) in samples)
        {
            double x = MarginLeft + ms / maxMs * plotW;
            double y = MarginTop + plotH - (double)done / totalTasks * plotH;
            points.Append(inv, $"{x:F1},{y:F1} ");
        }

        double tasksPerSec = totalTasks / Math.Max(maxMs / 1000.0, 1e-9);

        var svg = new StringBuilder();
        svg.AppendLine(inv, $"<svg xmlns='http://www.w3.org/2000/svg' width='{Width}' height='{Height}' style='background:#fff;font-family:sans-serif'>");
        svg.AppendLine(inv, $"<text x='{Width / 2}' y='20' text-anchor='middle' font-size='12'>{EscapeXml(title)}</text>");

        // gridlines + axis labels
        for (int t = 0; t <= 4; t++)
        {
            double gx = MarginLeft + plotW * t / 4.0;
            double gy = MarginTop + plotH * t / 4.0;
            svg.AppendLine(inv, $"<line x1='{gx:F1}' y1='{MarginTop}' x2='{gx:F1}' y2='{MarginTop + plotH}' stroke='#e0e0e0'/>");
            svg.AppendLine(inv, $"<line x1='{MarginLeft}' y1='{gy:F1}' x2='{MarginLeft + plotW}' y2='{gy:F1}' stroke='#e0e0e0'/>");
            svg.AppendLine(inv, $"<text x='{gx:F1}' y='{MarginTop + plotH + 18}' text-anchor='middle' font-size='11'>{maxMs * t / 4.0:F1}</text>");
            svg.AppendLine(inv, $"<text x='{MarginLeft - 8}' y='{gy + 4:F1}' text-anchor='end' font-size='11'>{totalTasks * (4 - t) / 4:N0}</text>");
        }

        // axes
        svg.AppendLine(inv, $"<line x1='{MarginLeft}' y1='{MarginTop}' x2='{MarginLeft}' y2='{MarginTop + plotH}' stroke='#000'/>");
        svg.AppendLine(inv, $"<line x1='{MarginLeft}' y1='{MarginTop + plotH}' x2='{MarginLeft + plotW}' y2='{MarginTop + plotH}' stroke='#000'/>");
        svg.AppendLine(inv, $"<text x='{MarginLeft + plotW / 2}' y='{Height - 10}' text-anchor='middle' font-size='12'>time (ms)</text>");
        svg.AppendLine(inv, $"<text x='16' y='{MarginTop + plotH / 2}' text-anchor='middle' font-size='12' transform='rotate(-90 16 {MarginTop + plotH / 2})'>work done (tasks)</text>");

        // ideal-linear reference line
        svg.AppendLine(inv, $"<line x1='{MarginLeft}' y1='{MarginTop + plotH:F1}' x2='{MarginLeft + plotW:F1}' y2='{MarginTop}' stroke='#aaa' stroke-dasharray='4 4'/>");

        // measured progress
        svg.AppendLine(inv, $"<polyline points='{points}' fill='none' stroke='#1f77b4' stroke-width='1.5'/>");

        // throughput annotation
        svg.AppendLine(inv, $"<text x='{MarginLeft + plotW - 6:F1}' y='{MarginTop + 14}' text-anchor='end' font-size='11' fill='#1f77b4'>{tasksPerSec / 1e6:F2} M tasks/s</text>");

        svg.AppendLine("</svg>");
        File.WriteAllText(path, svg.ToString());
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&apos;");
}
