"""campaign_results.json -> a self-contained, styled HTML report (report.html), opened in the browser.

Everything is inline (CSS, SVG charts, a few lines of JS, heatmap PNGs as data URIs), so the file can be
emailed, attached to a submission or opened offline. Colours follow a colour-blind-checked categorical
palette with fixed slots per algorithm, in light and dark mode.
"""

import base64
import html
import json
import os
import pathlib
import webbrowser

# The built-ins, in catalog order; each keeps its colour slot (1-6) in every report. Custom algorithms share 7-8.
ALGORITHM_ORDER = ["RandomBounce", "BoustrophedonSweep", "BoustrophedonCrawl", "Legacy.Spiral", "Legacy.WallFollow", "Legacy.Lawnmower"]
# Results from builds before algorithm ids used the bare pattern names.
LEGACY_IDS = {"Spiral": "Legacy.Spiral", "WallFollow": "Legacy.WallFollow", "Lawnmower": "Legacy.Lawnmower"}
ALGORITHM_LABELS = {"RandomBounce": "Random bounce", "BoustrophedonSweep": "Boustrophedon sweep",
                    "BoustrophedonCrawl": "Boustrophedon crawl", "Legacy.Spiral": "Spiral", "Legacy.WallFollow": "Wall follow",
                    "Legacy.Lawnmower": "Lawnmower"}
HEATMAP = "heatmap_floor_cleanliness.png"


def esc(value):
    return html.escape("" if value is None else str(value), quote=True)


def label_of(algorithm):
    algorithm = LEGACY_IDS.get(algorithm, algorithm)
    return ALGORITHM_LABELS.get(algorithm, str(algorithm))


def learn_labels(runs):
    """Custom algorithms are labelled with the display name the node recorded (runs[].algorithmName)."""
    for run in runs or []:
        algorithm, name = run.get("algorithm"), run.get("algorithmName")
        if algorithm and name and algorithm not in ALGORITHM_LABELS:
            ALGORITHM_LABELS[algorithm] = name


def slot_of(algorithm):
    """Colour follows the algorithm, never its rank: the same algorithm is the same colour in every chart."""
    algorithm = LEGACY_IDS.get(algorithm, algorithm)
    try:
        return ALGORITHM_ORDER.index(algorithm) + 1
    except ValueError:
        return 7 + (sum(map(ord, str(algorithm))) % 2)


def num(value):
    """Plain numbers without float noise: 320.0 -> 320, 0.0508 -> 0.0508."""
    return f"{value:g}" if isinstance(value, (int, float)) and not isinstance(value, bool) else value


def fmt(value, digits=1, suffix=""):
    if value is None:
        return "–"
    return f"{value:,.{digits}f}{suffix}"


def fmt_duration(seconds):
    if seconds is None or seconds < 0:
        return "–"
    seconds = int(round(seconds))
    hours, rest = divmod(seconds, 3600)
    minutes, secs = divmod(rest, 60)
    return f"{hours}:{minutes:02d}:{secs:02d}" if hours else f"{minutes}:{secs:02d}"


# ---------------------------------------------------------------------------------------------- data helpers

def sections(results):
    """Groups of the same floor plan and configuration, each with its algorithms in fixed order."""
    out = {}
    for group in results.get("groups", []):
        out.setdefault((group["floorPlan"], group["configuration"]), []).append(group)
    for members in out.values():
        members.sort(key=lambda g: slot_of(g["algorithm"]))
    return out


def representative_runs(results):
    """For each group, the succeeded run whose coverage is the median: what a 'typical' run looked like."""
    picks = {}
    for run in results.get("runs", []):
        if not run.get("succeeded"):
            continue
        picks.setdefault((run.get("floorPlan"), run.get("configuration"), run.get("algorithm")), []).append(run)
    chosen = {}
    for key, runs in picks.items():
        runs.sort(key=lambda r: (r.get("coveragePercent") or 0, r.get("runIndex")))
        chosen[key] = runs[(len(runs) - 1) // 2]
    return chosen


def shard_count(results, job):
    if job and job.get("execution", {}).get("shardCount"):
        return int(job["execution"]["shardCount"])
    return max(1, len(results.get("shards") or []) or 1)


def heatmap_path(base_dir, run, shards):
    if not run or not run.get("runFolder"):
        return None
    shard = f"shard-{run['runIndex'] % shards:04d}"
    return os.path.join(base_dir, "shards", shard, *run["runFolder"].split("/"), HEATMAP)


def image_data_uri(path):
    try:
        with open(path, "rb") as handle:
            return "data:image/png;base64," + base64.b64encode(handle.read()).decode("ascii")
    except OSError:
        return None


def polygon_area(points):
    if not points or len(points) < 3:
        return 0.0
    total = 0.0
    for i, a in enumerate(points):
        b = points[(i + 1) % len(points)]
        total += a.get("x", 0) * b.get("y", 0) - b.get("x", 0) * a.get("y", 0)
    return abs(total) / 2


# ---------------------------------------------------------------------------------------------- charts

def coverage_chart(groups, runs_by_group):
    """One row per algorithm: mean coverage bar, 95% CI whisker, and every run as a dot underneath."""
    label_w, plot_x0, plot_x1, width = 150, 160, 900, 1000
    row_h, top = 58, 10
    height = top + row_h * len(groups) + 30
    x = lambda pct: plot_x0 + (plot_x1 - plot_x0) * max(0.0, min(100.0, pct)) / 100.0

    parts = [f'<svg class="chart" viewBox="0 0 {width} {height}" role="img" '
             f'aria-label="Mean coverage per algorithm with 95% confidence interval and individual runs">']
    for tick in (0, 25, 50, 75, 100):
        tx = x(tick)
        parts.append(f'<line class="grid" x1="{tx:.1f}" y1="{top - 4}" x2="{tx:.1f}" y2="{height - 26}"/>')
        parts.append(f'<text class="tick" x="{tx:.1f}" y="{height - 10}" text-anchor="middle">{tick}%</text>')

    for row, group in enumerate(groups):
        cov = group["metrics"]["coveragePercent"]
        slot = slot_of(group["algorithm"])
        y = top + row * row_h
        bar_y, bar_h = y + 6, 14
        name = label_of(group["algorithm"])
        parts.append(f'<text class="rowlabel" x="{label_w}" y="{bar_y + 11}" text-anchor="end">{esc(name)}</text>')
        parts.append(f'<line class="baseline" x1="{plot_x0}" y1="{y + 2}" x2="{plot_x0}" y2="{y + row_h - 8}"/>')
        if cov["count"]:
            xm = x(cov["mean"])
            r = min(4, max(0.0, xm - plot_x0))
            path = (f"M{plot_x0},{bar_y} H{xm - r:.1f} Q{xm:.1f},{bar_y} {xm:.1f},{bar_y + r:.1f} "
                    f"V{bar_y + bar_h - r:.1f} Q{xm:.1f},{bar_y + bar_h} {xm - r:.1f},{bar_y + bar_h} H{plot_x0} Z")
            tip = (f"{name} · mean {cov['mean']:.1f}% · 95% CI {cov['ci95Low']:.1f}–{cov['ci95High']:.1f}% · "
                   f"SD {cov['stdDev']:.1f} · n {cov['count']}")
            parts.append(f'<g class="mark" tabindex="0" data-tip="{esc(tip)}">'
                         f'<rect class="hit" x="{plot_x0}" y="{y}" width="{plot_x1 - plot_x0}" height="{bar_h + 12}"/>'
                         f'<path class="s{slot}" d="{path}"/>')
            lo, hi, cy = x(cov["ci95Low"]), x(cov["ci95High"]), bar_y + bar_h / 2
            if hi - lo > 0.5:
                parts.append(f'<line class="ci" x1="{lo:.1f}" y1="{cy}" x2="{hi:.1f}" y2="{cy}"/>'
                             f'<line class="ci" x1="{lo:.1f}" y1="{cy - 5}" x2="{lo:.1f}" y2="{cy + 5}"/>'
                             f'<line class="ci" x1="{hi:.1f}" y1="{cy - 5}" x2="{hi:.1f}" y2="{cy + 5}"/>')
            parts.append("</g>")
            value_x = min(max(hi, xm) + 8, width - 4)
            anchor = "start" if value_x < width - 70 else "end"
            parts.append(f'<text class="value" x="{value_x:.1f}" y="{bar_y + 11}" text-anchor="{anchor}">'
                         f'{cov["mean"]:.1f}% <tspan class="muted">± {cov["ci95HalfWidth"]:.1f}</tspan></text>')
        else:
            parts.append(f'<text class="muted" x="{plot_x0 + 8}" y="{bar_y + 11}">no successful runs</text>')

        dot_y = bar_y + bar_h + 16
        for i, run in enumerate(runs_by_group.get(group["algorithm"], [])):
            cx = x(run.get("coveragePercent") or 0)
            cy = dot_y + ((i * 5) % 7 - 3) * 1.4
            tip = (f"{name} · seed {run.get('seed')} · {run.get('coveragePercent', 0):.1f}% · "
                   f"{run.get('endReason')} · {fmt_duration(run.get('simulatedSeconds'))} simulated")
            parts.append(f'<g class="mark" tabindex="0" data-tip="{esc(tip)}">'
                         f'<circle class="hit" cx="{cx:.1f}" cy="{cy:.1f}" r="12"/>'
                         f'<circle class="dot s{slot}" cx="{cx:.1f}" cy="{cy:.1f}" r="4"/></g>')
    parts.append("</svg>")
    return "".join(parts)


def legend(groups):
    items = "".join(f'<span class="key"><i class="sw s{slot_of(g["algorithm"])}"></i>{esc(label_of(g["algorithm"]))}</span>'
                    for g in groups)
    return (f'<div class="legend">{items}<span class="key"><i class="ci-key"></i>95% confidence interval</span>'
            f'<span class="key"><i class="dot-key"></i>one run</span></div>')


def stats_table(groups):
    head = ("<tr><th>Algorithm</th><th class=num>Runs</th><th class=num>Coverage % (mean ± 95% CI)</th>"
            "<th class=num>SD</th><th class=num>Fully cleaned %</th><th class=num>Simulated time</th>"
            "<th class=num>Battery used</th><th class=num>Distance (m)</th><th class=num>Revisits</th>"
            "<th class=num>Time to 90%</th></tr>")
    rows = []
    for g in groups:
        m = g["metrics"]
        cov, sec, bat = m["coveragePercent"], m["simulatedSeconds"], m["batteryUsed"]
        t90 = m["timeTo90"]
        failed = f' <span class="badge bad">{g["failed"]} failed</span>' if g["failed"] else ""
        t90_text = f"{fmt_duration(t90['mean'])} ({t90['count']}/{g['succeeded']})" if t90["count"] else "never"
        rows.append(
            f'<tr><td><i class="sw s{slot_of(g["algorithm"])}"></i>{esc(label_of(g["algorithm"]))}{failed}</td>'
            f'<td class=num>{g["succeeded"]}</td>'
            f'<td class=num><b>{fmt(cov["mean"])}</b> ± {fmt(cov["ci95HalfWidth"])}</td>'
            f'<td class=num>{fmt(cov["stdDev"])}</td>'
            f'<td class=num>{fmt(m["fullyCleanedPercent"]["mean"])}</td>'
            f'<td class=num>{fmt_duration(sec["mean"])} ± {fmt(sec["ci95HalfWidth"], 0)} s</td>'
            f'<td class=num>{fmt(bat["mean"], 0)} ± {fmt(bat["ci95HalfWidth"], 0)}</td>'
            f'<td class=num>{fmt(m["distanceMeters"]["mean"])}</td>'
            f'<td class=num>{fmt(m["revisitCount"]["mean"], 0)}</td>'
            f'<td class=num>{t90_text}</td></tr>')
    return f'<div class="tablewrap"><table>{head}{"".join(rows)}</table></div>'


# ---------------------------------------------------------------------------------------------- page

CSS = """
.viz-root{color-scheme:light;--plane:#f9f9f7;--surface:#fcfcfb;--ink:#0b0b0b;--ink2:#52514e;--muted:#898781;
--grid:#e1e0d9;--axis:#c3c2b7;--ring:rgba(11,11,11,.10);--s1:#2a78d6;--s2:#eb6834;--s3:#1baf7a;--s4:#eda100;
--s5:#e87ba4;--s6:#008300;--s7:#6250d6;--s8:#e34948;--good:#0ca30c;--critical:#d03b3b;--warn:#fab219}
@media (prefers-color-scheme:dark){:root:where(:not([data-theme="light"])) .viz-root{color-scheme:dark;--plane:#0d0d0d;
--surface:#1a1a19;--ink:#f0efec;--ink2:#c3c2b7;--muted:#898781;--grid:#2c2c2a;--axis:#383835;--ring:rgba(255,255,255,.10);
--s1:#3987e5;--s2:#d95926;--s3:#199e70;--s4:#c98500;--s5:#d55181;--s6:#008300;--s7:#9085e9;--s8:#e66767}}
:root[data-theme="dark"] .viz-root{color-scheme:dark;--plane:#0d0d0d;--surface:#1a1a19;--ink:#f0efec;--ink2:#c3c2b7;
--grid:#2c2c2a;--axis:#383835;--ring:rgba(255,255,255,.10);--s1:#3987e5;--s2:#d95926;--s3:#199e70;--s4:#c98500;
--s5:#d55181;--s7:#9085e9;--s8:#e66767}
*{box-sizing:border-box}html,body{margin:0}
.viz-root{background:var(--plane);color:var(--ink);font:14px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif;min-height:100vh}
.page{max-width:1080px;margin:0 auto;padding:32px 24px 64px}
.eyebrow{color:var(--muted);font-size:12px;letter-spacing:.06em;text-transform:uppercase;font-weight:600}
h1{font-size:28px;line-height:1.2;margin:4px 0 6px}h2{font-size:18px;margin:0}h3{font-size:14px;margin:18px 0 8px;color:var(--ink2)}
.meta{color:var(--ink2);margin:0 0 20px}.sub{color:var(--ink2);margin:2px 0 14px}
.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin:0 0 20px}
.tile{background:var(--surface);border:1px solid var(--ring);border-radius:12px;padding:14px 16px}
.tile .k{color:var(--ink2);font-size:12px}.tile .v{font-size:26px;font-weight:600;line-height:1.2;margin-top:2px}
.tile .d{color:var(--muted);font-size:12px}
nav.toc{display:flex;flex-wrap:wrap;gap:6px 14px;margin:0 0 20px;font-size:13px}
nav.toc a{color:var(--ink2);text-decoration:none;border-bottom:1px solid var(--axis)}nav.toc a:hover{color:var(--ink)}
.card{background:var(--surface);border:1px solid var(--ring);border-radius:14px;padding:20px 22px;margin:0 0 18px}
.chart{width:100%;height:auto;display:block;margin:6px 0 4px}
.chart .grid{stroke:var(--grid);stroke-width:1}.chart .baseline{stroke:var(--axis);stroke-width:1}
.chart .tick{fill:var(--muted);font-size:11px;font-variant-numeric:tabular-nums}
.chart .rowlabel{fill:var(--ink);font-size:13px}.chart .value{fill:var(--ink);font-size:12px;font-weight:600}
.chart .muted,.muted{fill:var(--muted);color:var(--muted);font-weight:400}
.chart .ci{stroke:var(--ink);stroke-width:1.5}.chart .hit{fill:transparent}
.chart .dot{stroke:var(--surface);stroke-width:2}
.chart .mark{cursor:default;outline:none}.chart .mark:hover path,.chart .mark:focus path{filter:brightness(1.12)}
.chart .mark:hover .dot,.chart .mark:focus .dot{stroke:var(--ink)}
.s1{fill:var(--s1);background:var(--s1)}.s2{fill:var(--s2);background:var(--s2)}.s3{fill:var(--s3);background:var(--s3)}
.s4{fill:var(--s4);background:var(--s4)}.s5{fill:var(--s5);background:var(--s5)}.s6{fill:var(--s6);background:var(--s6)}
.s7{fill:var(--s7);background:var(--s7)}.s8{fill:var(--s8);background:var(--s8)}
.legend{display:flex;flex-wrap:wrap;gap:4px 16px;color:var(--ink2);font-size:12px;margin:4px 0}
.key{display:inline-flex;align-items:center;gap:6px}
i.sw{display:inline-block;width:12px;height:12px;border-radius:3px;margin-right:8px;vertical-align:-1px}
.legend i.sw{margin-right:0}
.ci-key{display:inline-block;width:16px;height:8px;border-left:1.5px solid var(--ink);border-right:1.5px solid var(--ink);
background:linear-gradient(var(--ink),var(--ink)) center/100% 1.5px no-repeat}
.dot-key{display:inline-block;width:9px;height:9px;border-radius:50%;background:var(--ink2)}
.tablewrap{overflow-x:auto;margin-top:8px}
table{border-collapse:collapse;width:100%;font-size:13px}
th{text-align:left;color:var(--ink2);font-weight:600;font-size:12px;padding:8px 10px;border-bottom:1px solid var(--axis);white-space:nowrap}
td{padding:7px 10px;border-bottom:1px solid var(--grid);white-space:nowrap}
.num{text-align:right;font-variant-numeric:tabular-nums}
th.sortable{cursor:pointer;user-select:none}th.sortable:hover{color:var(--ink)}
th.sortable[aria-sort="ascending"]::after{content:" ▲";font-size:10px}th.sortable[aria-sort="descending"]::after{content:" ▼";font-size:10px}
.badge{display:inline-block;font-size:11px;font-weight:600;padding:1px 7px;border-radius:999px;margin-left:6px;border:1px solid currentColor}
.badge.bad{color:var(--critical)}.badge.ok{color:var(--good)}
.status{display:inline-flex;align-items:center;gap:6px}
.heatmaps{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:14px;margin-top:6px}
figure{margin:0}figure img{width:100%;border-radius:8px;border:1px solid var(--ring);background:var(--plane);image-rendering:pixelated}
figcaption{color:var(--ink2);font-size:12px;margin-top:4px}
details{margin-top:4px}summary{cursor:pointer;color:var(--ink2);font-weight:600}
.note{color:var(--muted);font-size:12px;margin-top:10px}
.problems li{margin:4px 0}
#tip{position:fixed;pointer-events:none;z-index:10;background:var(--surface);color:var(--ink);border:1px solid var(--ring);
border-radius:8px;padding:6px 10px;font-size:12px;box-shadow:0 4px 16px rgba(0,0,0,.18);display:none;max-width:360px}
footer{color:var(--muted);font-size:12px;margin-top:28px}
@media print{.viz-root{background:#fff}.card,.tile{break-inside:avoid}nav.toc{display:none}}
"""

JS = """
(function(){
  var tip=document.getElementById('tip');
  function show(el,x,y){tip.textContent=el.getAttribute('data-tip');tip.style.display='block';
    var w=tip.offsetWidth,h=tip.offsetHeight;tip.style.left=Math.min(x+14,innerWidth-w-8)+'px';tip.style.top=Math.max(8,y-h-10)+'px';}
  document.querySelectorAll('[data-tip]').forEach(function(el){
    el.addEventListener('pointermove',function(e){show(el,e.clientX,e.clientY);});
    el.addEventListener('pointerleave',function(){tip.style.display='none';});
    el.addEventListener('focus',function(){var r=el.getBoundingClientRect();show(el,r.left+r.width/2,r.top);});
    el.addEventListener('blur',function(){tip.style.display='none';});
  });
  document.querySelectorAll('table.sortable').forEach(function(table){
    table.querySelectorAll('th').forEach(function(th,col){
      th.classList.add('sortable');
      th.addEventListener('click',function(){
        var asc=th.getAttribute('aria-sort')!=='ascending';
        table.querySelectorAll('th').forEach(function(o){o.removeAttribute('aria-sort');});
        th.setAttribute('aria-sort',asc?'ascending':'descending');
        var body=table.tBodies[0],rows=Array.prototype.slice.call(body.rows);
        rows.sort(function(a,b){var x=a.cells[col].getAttribute('data-v'),y=b.cells[col].getAttribute('data-v');
          var nx=parseFloat(x),ny=parseFloat(y);var c=(!isNaN(nx)&&!isNaN(ny))?nx-ny:String(x).localeCompare(String(y));
          return asc?c:-c;});
        rows.forEach(function(r){body.appendChild(r);});
      });
    });
  });
})();
"""


def tile(key, value, detail=""):
    return f'<div class="tile"><div class="k">{esc(key)}</div><div class="v">{value}</div><div class="d">{esc(detail)}</div></div>'


def configurations_card(job):
    configs = (job or {}).get("configurations") or []
    if not configs:
        return ""
    fields = [
        ("Drive speed (m/s)", "vacuum", "driveSpeed"), ("Reverse speed (m/s)", "vacuum", "reverseSpeed"),
        ("Turn speed (°/s)", "vacuum", "turnSpeed"), ("Vacuum power", "vacuum", "vacuumPower"),
        ("Battery capacity", "battery", "capacity"), ("Energy per metre", "battery", "distanceEnergyPerMeter"),
        ("Energy per degree", "battery", "turnEnergyPerDegree"), ("Energy per second (on)", "battery", "operatingEnergyPerSecond"),
        ("Energy per second (stalled)", "battery", "stalledEnergyPerSecond"), ("Cell size (m)", "simulation", "cellSizeMeters"),
        ("Target cleanliness", "simulation", "targetMaxClean"), ("Simulated-time cap (s)", "simulation", "maxSimulatedSeconds"),
        ("Telemetry interval (s)", "simulation", "telemetryInterval"),
    ]
    head = "<tr><th>Setting</th>" + "".join(f"<th class=num>{esc(c.get('name'))}</th>" for c in configs) + "</tr>"
    rows = []
    for label, section, key in fields:
        values = [(c.get(section) or {}).get(key) for c in configs]
        rows.append(f"<tr><td>{esc(label)}</td>" + "".join(f"<td class=num>{esc(num(v))}</td>" for v in values) + "</tr>")
    efforts = ["; ".join(f"{e.get('floor')} {num(e.get('effort'))}" for e in (c.get("floorEfforts") or [])) or "–" for c in configs]
    rows.append("<tr><td>Cleaning-effort overrides</td>" + "".join(f"<td class=num>{esc(e)}</td>" for e in efforts) + "</tr>")
    floors = (job or {}).get("floorTypes") or []
    floor_note = (f"Custom floor coverings: {esc(', '.join(f.get('name', '?') for f in floors))}." if floors
                  else "Floor coverings: the simulator's stock six.")
    return (f'<section class="card" id="configurations"><h2>Configurations</h2>'
            f'<p class="sub">Every setting the runs used, from job.json. {floor_note}</p>'
            f'<div class="tablewrap"><table>{head}{"".join(rows)}</table></div></section>')


def floor_plans_card(job):
    plans = (job or {}).get("floorPlans") or []
    if not plans:
        return ""
    rows = []
    for plan in plans:
        rooms = [r for r in plan.get("rooms") or [] if r]
        area = sum(polygon_area(r.get("outline")) for r in rooms)
        floors = sorted({r.get("floor") or f"#{r.get('floorIndex', 0)}" for r in rooms})
        furniture = [o for o in plan.get("obstacles") or [] if o and o.get("kind") != 6]
        cats = [o for o in plan.get("obstacles") or [] if o and o.get("kind") == 6]
        rows.append(f"<tr><td><b>{esc(plan.get('name'))}</b></td><td class=num>{len(rooms)}</td><td class=num>{area:.1f}</td>"
                    f"<td>{esc(', '.join(floors))}</td><td class=num>{len(plan.get('doorways') or [])}</td>"
                    f"<td class=num>{len(furniture)}</td><td class=num>{len(cats)}</td></tr>")
    head = ("<tr><th>Floor plan</th><th class=num>Rooms</th><th class=num>Floor area (m²)</th><th>Coverings</th>"
            "<th class=num>Doorways</th><th class=num>Furniture</th><th class=num>Cats</th></tr>")
    return (f'<section class="card" id="floorplans"><h2>Floor plans</h2>'
            f'<div class="tablewrap"><table>{head}{"".join(rows)}</table></div></section>')


def problems_card(results):
    failed = [r for r in results.get("runs", []) if not r.get("succeeded")]
    missing = results["totals"].get("missingRuns") or []
    problems = results.get("problems") or []
    if not (failed or missing or problems):
        return ('<section class="card" id="problems"><h2>Problems</h2>'
                '<p class="sub"><span class="badge ok">✓ none</span> Every planned run finished successfully.</p></section>')
    items = [f"<li><b>Run {r.get('runIndex')}</b> · {esc(r.get('floorPlan'))} / {esc(r.get('configuration'))} / "
             f"{esc(label_of(r.get('algorithm')))} / seed {esc(r.get('seed'))}: {esc(r.get('error') or r.get('endReason'))}</li>"
             for r in failed[:200]]
    if missing:
        shown = ", ".join(str(i) for i in missing[:60]) + (" …" if len(missing) > 60 else "")
        items.append(f"<li><b>{len(missing)} run(s) never came back</b> (run index {esc(shown)}). "
                     f"<code>python -m vacuumctl retry &lt;campaignId&gt;</code> re-runs only those nodes.</li>")
    items += [f"<li>{esc(p)}</li>" for p in problems]
    return (f'<section class="card" id="problems"><h2>Problems</h2>'
            f'<p class="sub"><span class="badge bad">⚠ {len(failed)} failed · {len(missing)} missing</span> '
            f'Failed runs are left out of the statistics.</p><ul class="problems">{"".join(items)}</ul></section>')


def runs_card(results):
    cols = [("Run", "runIndex", "num"), ("Floor plan", "floorPlan", ""), ("Config", "configuration", ""),
            ("Algorithm", "algorithm", ""), ("Seed", "seed", "num"), ("Status", "endReason", ""),
            ("Coverage %", "coveragePercent", "num"), ("Fully cleaned %", "fullyCleanedPercent", "num"),
            ("Simulated", "simulatedSeconds", "num"), ("Real (s)", "realSeconds", "num"),
            ("Battery used", "batteryUsed", "num"), ("Distance (m)", "distanceMeters", "num"), ("Revisits", "revisitCount", "num")]
    head = "".join(f'<th class="{c}">{esc(t)}</th>' for t, _, c in cols)
    rows = []
    for run in results.get("runs", []):
        cells = []
        for _, key, cls in cols:
            value = run.get(key)
            if key == "algorithm":
                text = f'<i class="sw s{slot_of(value)}"></i>{esc(label_of(value))}'
            elif key == "endReason":
                text = esc(value) if run.get("succeeded") else f'<span class="badge bad">⚠ {esc(value)}</span>'
            elif key == "simulatedSeconds":
                text = fmt_duration(value)
            elif isinstance(value, float):
                text = fmt(value, 1)
            else:
                text = esc(value)
            cells.append(f'<td class="{cls}" data-v="{esc(value)}">{text}</td>')
        rows.append("<tr>" + "".join(cells) + "</tr>")
    return (f'<section class="card" id="runs"><h2>Every run</h2><p class="sub">{len(rows)} runs · click a column to sort.</p>'
            f'<details><summary>Show the table</summary><div class="tablewrap"><table class="sortable"><thead><tr>{head}</tr></thead>'
            f'<tbody>{"".join(rows)}</tbody></table></div></details></section>')


def nodes_card(results, job=None):
    shards = results.get("shards") or []
    count = max(1, len(shards))
    planned = {}
    for run in (job or {}).get("runs") or []:
        planned[run["runIndex"] % count] = planned.get(run["runIndex"] % count, 0) + 1
    returned = {}
    for run in results.get("runs") or []:
        returned[run["runIndex"] % count] = returned.get(run["runIndex"] % count, 0) + 1
    hosts = {h.get("shardIndex"): h for h in results.get("hosts") or []}
    if not shards:
        return ""
    rows = []
    for s in shards:
        h = hosts.get(s.get("shardIndex"), {})
        rows.append(f"<tr><td class=num>{esc(s.get('shardIndex'))}</td><td>{esc(s.get('status'))}</td>"
                    f"<td class=num>{esc(s.get('returned', returned.get(s.get('shardIndex'), 0)))}/{esc(s.get('planned', planned.get(s.get('shardIndex'), '–')))}</td>"
                    f"<td>{esc(h.get('operatingSystem') or h.get('platform') or '–')}</td>"
                    f"<td>{esc(h.get('processorType') or '–')}</td><td class=num>{esc(h.get('processorCount') or '–')}</td>"
                    f"<td>{esc(h.get('unityVersion') or '–')}</td></tr>")
    head = ("<tr><th class=num>Node</th><th>Status</th><th class=num>Runs back</th><th>OS</th><th>CPU</th>"
            "<th class=num>Cores</th><th>Unity</th></tr>")
    return (f'<section class="card" id="nodes"><h2>Compute nodes</h2>'
            f'<div class="tablewrap"><table>{head}{"".join(rows)}</table></div></section>')


def build_html(results, job=None, base_dir="."):
    totals = results["totals"]
    secs = sections(results)
    reps = representative_runs(results)
    shards = shard_count(results, job)
    runs = results.get("runs", [])
    learn_labels(runs)

    best = None
    for g in results.get("groups", []):
        mean = g["metrics"]["coveragePercent"]["mean"]
        if mean is not None and (best is None or mean > best[0]):
            best = (mean, g)
    sims = [r.get("simulatedSeconds") or 0 for r in runs if r.get("succeeded")]
    reals = [r.get("realSeconds") or 0 for r in runs if r.get("succeeded")]
    speedup = (sum(sims) / sum(reals)) if sum(reals) > 0 else None

    tiles = [
        tile("Runs returned", f"{totals['returnedRuns']}<span class=muted style='font-size:16px'> / {totals['plannedRuns']}</span>",
             f"on {len(results.get('shards') or []) or shards} node(s)"),
        tile("Succeeded", f"{totals['succeededRuns']}", "used in the statistics"),
        tile("Failed or missing", f"{totals['failedRuns'] + len(totals['missingRuns'])}",
             "✓ none" if not (totals['failedRuns'] or totals['missingRuns']) else "⚠ see Problems"),
    ]
    if best:
        g = best[1]
        tiles.append(tile("Best mean coverage", f"{best[0]:.1f}%", f"{label_of(g['algorithm'])} · {g['floorPlan']} · {g['configuration']}"))
    if speedup:
        tiles.append(tile("Simulated vs real time", f"{speedup:.0f}×", f"{fmt_duration(sum(sims))} simulated in total"))
    if results.get("wallClockSeconds"):
        tiles.append(tile("Wall-clock time", fmt_duration(results["wallClockSeconds"]), "whole campaign"))

    toc = []
    body = []
    for index, ((plan, config), groups) in enumerate(sorted(secs.items(), key=lambda kv: (str(kv[0][0]), str(kv[0][1])))):
        anchor = f"g{index}"
        toc.append(f'<a href="#{anchor}">{esc(plan)} · {esc(config)}</a>')
        by_alg = {}
        for run in runs:
            if run.get("succeeded") and run.get("floorPlan") == plan and run.get("configuration") == config:
                by_alg.setdefault(run.get("algorithm"), []).append(run)
        seeds = sorted({s for g in groups for s in g.get("seeds") or []})
        seed_text = f"seeds {seeds[0]}–{seeds[-1]}" if seeds else "no successful runs"
        figures = []
        for g in groups:
            run = reps.get((plan, config, g["algorithm"]))
            uri = image_data_uri(heatmap_path(base_dir, run, shards)) if run else None
            if uri:
                figures.append(f'<figure><img src="{uri}" alt="Floor cleanliness heatmap, {esc(label_of(g["algorithm"]))}, seed {esc(run.get("seed"))}">'
                               f'<figcaption><i class="sw s{slot_of(g["algorithm"])}"></i>{esc(label_of(g["algorithm"]))} · median run · '
                               f'seed {esc(run.get("seed"))} · {run.get("coveragePercent", 0):.1f}%</figcaption></figure>')
        heatmaps = (f'<h3>What a typical run cleaned</h3><div class="heatmaps">{"".join(figures)}</div>'
                    f'<p class="note">Floor Cleanliness heatmaps of each algorithm\'s median-coverage run, in the simulator\'s own colours.</p>'
                    if figures else "")
        body.append(f'<section class="card" id="{anchor}"><h2>{esc(plan)} <span class="muted">·</span> {esc(config)}</h2>'
                    f'<p class="sub">{len(groups)} algorithm(s) on identical seeds · {seed_text}</p>'
                    f'<h3>Coverage</h3>{legend(groups)}{coverage_chart(groups, by_alg)}'
                    f'<h3>Statistics</h3>{stats_table(groups)}{heatmaps}</section>')

    extra = [problems_card(results), configurations_card(job), floor_plans_card(job), runs_card(results), nodes_card(results, job)]
    toc += ['<a href="#problems">Problems</a>', '<a href="#configurations">Configurations</a>',
            '<a href="#floorplans">Floor plans</a>', '<a href="#runs">Every run</a>', '<a href="#nodes">Nodes</a>']

    description = (job or {}).get("description")
    return f"""<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{esc(results.get('campaignName'))} · campaign report</title><style>{CSS}</style></head>
<body><div class="viz-root"><div class="page">
<div class="eyebrow">Robot Vacuum Simulator · Campaign report</div>
<h1>{esc(results.get('campaignName'))}</h1>
<p class="meta">{esc(results.get('campaignId'))} · generated {esc((results.get('generatedUtc') or '')[:16].replace('T', ' '))} UTC
{(' · ' + esc(description)) if description else ''}</p>
<div class="tiles">{''.join(tiles)}</div>
<nav class="toc">{''.join(toc)}</nav>
{''.join(body)}
{''.join(extra)}
<footer>Statistics: sample variance (n − 1) and 95% confidence intervals of the mean (Student's t, two-sided), as in the simulator's own batch view.
Failed runs are excluded; "time to 90%" averages only the runs that reached 90%. Every number here comes from campaign_results.json
in the same folder, which also lists each run so the statistics can be recomputed by hand.</footer>
</div><div id="tip" role="tooltip"></div></div><script>{JS}</script></body></html>"""


def write_report(results_path, open_browser=True, job=None):
    """Writes report.html next to campaign_results.json and (optionally) opens it. Returns the path."""
    if os.path.isdir(results_path):
        results_path = os.path.join(results_path, "campaign_results.json")
    base_dir = os.path.dirname(os.path.abspath(results_path))
    with open(results_path, encoding="utf-8") as handle:
        results = json.load(handle)
    if job is None:
        try:
            with open(os.path.join(base_dir, "job.json"), encoding="utf-8") as handle:
                job = json.load(handle)
        except (OSError, ValueError):
            job = None
    path = os.path.join(base_dir, "report.html")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(build_html(results, job, base_dir))
    print(f"Report: {path}")
    if open_browser:
        try:
            webbrowser.open(pathlib.Path(path).resolve().as_uri())
        except Exception as error:  # a headless machine has no browser; the file is still there
            print(f"(Could not open a browser: {error})")
    return path
