using System.Net;
using System.Text;
using System.Text.Json;

namespace Testerss;

/// <summary>
/// Minimal self-hosted monitoring dashboard. Runs an HttpListener on a
/// background thread; serves an embedded HTML page at "/" that polls
/// "/metrics" (JSON) twice a second. The engine is never touched — snapshots
/// are assembled entirely on the HTTP thread from lock-free counters.
/// </summary>
internal sealed class DashboardServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly LiveMetrics _metrics;
    private readonly Thread _thread;
    private volatile bool _stopping;

    public string Url { get; }

    public DashboardServer(LiveMetrics metrics, int port)
    {
        _metrics = metrics;
        Url = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "dashboard-http" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stopping)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (Exception) when (_stopping) { return; }
            catch (HttpListenerException) { return; }

            try
            {
                Handle(ctx);
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* client gone */ }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        byte[] body;
        string contentType;

        if (path == "/metrics")
        {
            var snapshot = _metrics.Snapshot();
            body = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonContext.Default.MetricsSnapshot);
            contentType = "application/json";
        }
        else
        {
            body = Encoding.UTF8.GetBytes(Html);
            contentType = "text/html; charset=utf-8";
        }

        ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body);
        ctx.Response.OutputStream.Close();
    }

    public void Dispose()
    {
        _stopping = true;
        try { _listener.Stop(); } catch { /* already stopped */ }
        _listener.Close();
    }

    private const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>delta-lag soak monitor</title>
        <style>
          :root { color-scheme: dark; }
          body { background:#0d1117; color:#c9d1d9; font-family:'Segoe UI',sans-serif; margin:0; padding:20px; }
          h1 { font-size:18px; margin:0 0 4px; color:#e6edf3; }
          .sub { color:#8b949e; font-size:12px; margin-bottom:16px; }
          .grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(180px,1fr)); gap:10px; margin-bottom:16px; }
          .card { background:#161b22; border:1px solid #30363d; border-radius:8px; padding:12px 14px; }
          .card .label { font-size:11px; color:#8b949e; text-transform:uppercase; letter-spacing:.06em; }
          .card .value { font-size:22px; font-weight:600; margin-top:4px; color:#e6edf3; font-variant-numeric:tabular-nums; }
          .card .value.ok { color:#3fb950; } .card .value.bad { color:#f85149; }
          .bar { background:#21262d; border-radius:4px; height:10px; overflow:hidden; margin-top:8px; }
          .bar > div { background:#1f6feb; height:100%; transition:width .4s; }
          .wide { grid-column:1 / -1; }
          canvas { width:100%; height:90px; display:block; }
          table { width:100%; border-collapse:collapse; font-size:12px; }
          th,td { padding:5px 8px; text-align:right; border-bottom:1px solid #21262d; font-variant-numeric:tabular-nums; }
          th { color:#8b949e; font-weight:500; } td:first-child,th:first-child { text-align:left; }
          .pass { color:#3fb950; } .fail { color:#f85149; font-weight:700; }
          .spec { font-family:Consolas,monospace; font-size:12px; color:#79c0ff; word-break:break-all; }
          .offline { color:#f85149; font-weight:600; }
        </style>
        </head>
        <body>
        <h1>delta-lag soak monitor</h1>
        <div class="sub" id="status">connecting&hellip;</div>

        <div class="grid" id="cards"></div>

        <div class="card wide">
          <div class="label">throughput (tasks/s, last 2 min)</div>
          <canvas id="spark" width="1200" height="90"></canvas>
        </div>

        <div class="card wide" style="margin-top:10px">
          <div class="label">current iteration</div>
          <div class="spec" id="spec"></div>
          <div class="bar"><div id="prog" style="width:0%"></div></div>
        </div>

        <div class="card wide" style="margin-top:10px">
          <div class="label">recent iterations</div>
          <table>
            <thead><tr><th>#</th><th>seed</th><th>nodes</th><th>edges</th><th>cycles</th><th>thr</th><th>slack</th><th>strategy</th><th>ms</th><th>M tasks/s</th><th>result</th></tr></thead>
            <tbody id="rows"></tbody>
          </table>
        </div>

        <script>
        const hist = [];            // {t, done+total based inst rate}
        let prev = null;
        let lastCpu = null;

        const fmt = (v, d=0) => v.toLocaleString(undefined, {maximumFractionDigits:d});
        const card = (label, value, cls='') =>
          `<div class="card"><div class="label">${label}</div><div class="value ${cls}">${value}</div></div>`;

        async function tick() {
          let m;
          try {
            m = await (await fetch('/metrics')).json();
            document.getElementById('status').textContent =
              `live \u2022 uptime ${new Date(m.UptimeSeconds*1000).toISOString().substr(11,8)} \u2022 refreshed ${new Date().toLocaleTimeString()}`;
          } catch {
            document.getElementById('status').innerHTML = '<span class="offline">offline — soak process not reachable</span>';
            return;
          }

          // instantaneous throughput from cumulative counters (client-side, zero engine cost)
          const now = performance.now();
          const cum = m.TotalTasksCompleted + m.CurrentDone;
          let inst = 0;
          if (prev) {
            const dt = (now - prev.t) / 1000;
            if (dt > 0) inst = Math.max(0, (cum - prev.cum) / dt);
          }
          // process CPU % from cumulative CPU seconds
          let cpuPct = 0;
          if (lastCpu) {
            const dt = (now - lastCpu.t) / 1000;
            if (dt > 0) cpuPct = Math.min(100, 100 * (m.TotalCpuSeconds - lastCpu.cpu) / dt / m.ProcessorCount);
          }
          prev = { t: now, cum };
          lastCpu = { t: now, cpu: m.TotalCpuSeconds };

          hist.push(inst);
          if (hist.length > 240) hist.shift();

          document.getElementById('cards').innerHTML =
            card('iterations', fmt(m.Iterations)) +
            card('failures', fmt(m.Failures), m.Failures ? 'bad' : 'ok') +
            card('total tasks', fmt(m.TotalTasksCompleted)) +
            card('avg tasks/s', fmt(m.AvgTasksPerSec/1e6, 2) + ' M') +
            card('live tasks/s', fmt(inst/1e6, 2) + ' M') +
            card('process cpu', fmt(cpuPct, 1) + ' %') +
            card('working set', fmt(m.WorkingSetMb, 1) + ' MB') +
            card('gc heap', fmt(m.GcHeapMb, 1) + ' MB') +
            card('gc committed', fmt(m.GcCommittedMb, 1) + ' MB') +
            card('gc gen0/1/2', `${fmt(m.Gen0Collections)}/${fmt(m.Gen1Collections)}/${fmt(m.Gen2Collections)}`) +
            card('gc pause', fmt(m.PauseTimePct, 2) + ' %') +
            card('threads', fmt(m.ThreadCount)) +
            card('pool threads', fmt(m.ThreadPoolThreads)) +
            card('pending work', fmt(m.PendingWorkItems));

          document.getElementById('spec').textContent = m.CurrentSpec;
          document.getElementById('prog').style.width = m.CurrentProgressPct.toFixed(1) + '%';

          document.getElementById('rows').innerHTML = m.Recent.slice(0, 20).map(r =>
            `<tr><td>${r.Iteration}</td><td>${r.Seed}</td><td>${r.Nodes}</td><td>${r.Edges}</td>` +
            `<td>${fmt(r.Cycles)}</td><td>${r.Threads}</td><td>${r.Slack}</td><td>${r.Strategy}</td>` +
            `<td>${r.ElapsedMs.toFixed(1)}</td><td>${(r.TasksPerSec/1e6).toFixed(2)}</td>` +
            `<td class="${r.Passed ? 'pass' : 'fail'}">${r.Passed ? 'PASS' : 'FAIL'}</td></tr>`).join('');

          drawSpark();
        }

        function drawSpark() {
          const c = document.getElementById('spark'), g = c.getContext('2d');
          g.clearRect(0, 0, c.width, c.height);
          if (hist.length < 2) return;
          const max = Math.max(...hist, 1);
          g.strokeStyle = '#1f6feb'; g.lineWidth = 2; g.beginPath();
          hist.forEach((v, i) => {
            const x = i / (hist.length - 1) * c.width;
            const y = c.height - (v / max) * (c.height - 6) - 3;
            i ? g.lineTo(x, y) : g.moveTo(x, y);
          });
          g.stroke();
          g.fillStyle = '#8b949e'; g.font = '11px sans-serif';
          g.fillText(`peak ${(max/1e6).toFixed(2)} M/s`, 6, 14);
        }

        tick();
        setInterval(tick, 500);
        </script>
        </body>
        </html>
        """;
}
