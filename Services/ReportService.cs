using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class ReportService
    {
        // ════════════════════════════════════════════════════════════════════
        // STANDARD REPORT  (single template, bilingual)
        // ════════════════════════════════════════════════════════════════════
        public async Task<string> GenerateHtmlReportAsync(
            SystemSummary summary,
            List<EventLogEntry> recentEvents,
            List<CrashEntry> crashes,
            List<DiskHealthEntry> disks,
            List<RamEntry> ramModules,
            List<TemperatureEntry> temps,
            string language = "en")
        {
            return await Task.Run(() =>
            {
                bool ro   = language == "ro";
                string L(string en, string r) => ro ? r : en;
                string Row(string lbl, string val) =>
                    $"<div class='row'><span class='lbl'>{lbl}</span><span class='val'>{System.Web.HttpUtility.HtmlEncode(val)}</span></div>";

                var sb  = new StringBuilder();
                var now = DateTime.Now;

                int critCount  = recentEvents.Count(e => e.Level is "Critical" or "Error");
                int warnCount  = recentEvents.Count(e => e.Level == "Warning");
                int crashCount = crashes.Count(c => c.FileName != "Niciun crash detectat" && c.FileName != "No crash detected");

                string health = crashCount > 2 || disks.Any(d => d.HealthPercent < 30) || critCount > 20
                    ? L("⚠ Critical", "⚠ Critică")
                    : crashCount > 0 || disks.Any(d => d.HealthPercent < 70) || critCount > 5
                        ? L("⚠ Warning", "⚠ Atenție")
                        : L("✔ Good", "✔ Bună");
                string healthCls = health.Contains("Crit") ? "health-bad"
                                 : health.Contains("Warn") || health.Contains("Aten") ? "health-warn"
                                 : "health-good";

                sb.Append($@"<!DOCTYPE html>
<html lang='{(ro ? "ro" : "en")}'>
<head>
<meta charset='UTF-8'>
<title>SMDWin — {L("System Report", "Raport Sistem")} — {summary.ComputerName} — {now:dd.MM.yyyy}</title>
<style>
  *{{box-sizing:border-box;margin:0;padding:0}}
  body{{font-family:'Segoe UI',Arial,sans-serif;background:#f0f4f8;color:#1a202c;padding:14px}}
  .container{{max-width:960px;margin:0 auto}}
  .header{{background:linear-gradient(135deg,#1e3a8a,#3b82f6);color:white;padding:14px 18px;border-radius:10px;margin-bottom:12px}}
  .header h1{{font-size:18px;font-weight:700;margin-bottom:3px}}
  .header p{{font-size:11px;opacity:.85;margin-top:2px}}
  .health-badge{{display:inline-block;padding:3px 12px;border-radius:16px;font-weight:700;font-size:11px;margin-top:6px}}
  .health-good{{background:#dcfce7;color:#166534}}.health-warn{{background:#fef9c3;color:#854d0e}}.health-bad{{background:#fee2e2;color:#991b1b}}
  .grid{{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-bottom:10px}}
  .grid-3{{display:grid;grid-template-columns:1fr 1fr 1fr;gap:10px;margin-bottom:10px}}
  .card{{background:white;border-radius:10px;padding:11px 13px;box-shadow:0 1px 3px rgba(0,0,0,.06)}}
  .card h2{{font-size:12px;font-weight:700;color:#1e3a8a;border-bottom:1px solid #e5edff;padding-bottom:5px;margin-bottom:8px;display:flex;align-items:center;gap:5px}}
  .row{{display:flex;justify-content:space-between;padding:3px 0;border-bottom:1px solid #f1f5f9;font-size:11px}}
  .row:last-child{{border-bottom:none}}
  .lbl{{color:#64748b;font-weight:500}}.val{{color:#0f172a;font-weight:600;text-align:right;max-width:65%}}
  .stat-card{{background:white;border-radius:12px;padding:18px;text-align:center;box-shadow:0 1px 4px rgba(0,0,0,.07)}}
  .stat-num{{font-size:38px;font-weight:800;line-height:1.1}}
  .stat-label{{font-size:11px;color:#64748b;margin-top:5px}}
  .green{{color:#16a34a}}.yellow{{color:#d97706}}.red{{color:#dc2626}}.blue{{color:#2563eb}}
  table{{width:100%;border-collapse:collapse;font-size:12px}}
  th{{background:#eff6ff;color:#1e3a8a;padding:7px 10px;text-align:left;font-weight:600}}
  td{{padding:6px 10px;border-bottom:1px solid #f1f5f9}}
  tr:last-child td{{border-bottom:none}}
  .tag{{display:inline-block;padding:2px 8px;border-radius:10px;font-size:11px;font-weight:600}}
  .tag-ok{{background:#dcfce7;color:#166534}}.tag-err{{background:#fee2e2;color:#991b1b}}
  .tag-warn{{background:#fef9c3;color:#854d0e}}.tag-info{{background:#eff6ff;color:#1e40af}}
  .progress-bar{{background:#e5e7eb;border-radius:4px;height:7px;margin-top:5px}}
  .progress-fill{{height:7px;border-radius:4px}}
  .section{{margin-bottom:18px}}
  .temp-badge{{display:inline-block;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:8px 14px;text-align:center;min-width:110px}}
  .footer{{text-align:center;color:#94a3b8;font-size:11px;margin-top:28px;padding-top:14px;border-top:1px solid #e2e8f0}}
  @media print{{body{{background:white;padding:0}}.card{{box-shadow:none;border:1px solid #e2e8f0}}}}
</style>
</head>
<body>
<div class='container'>

<div class='header'>
  <h1>🖥 {L("System Diagnostic Report", "Raport Diagnosticare Sistem")}</h1>
  <p>{L("Computer", "Calculator")}: <strong>{summary.ComputerName}</strong> &nbsp;·&nbsp; {summary.Manufacturer} {summary.Model}</p>
  <p>{L("Generated", "Generat")}: {now:dd MMMM yyyy, HH:mm:ss} &nbsp;·&nbsp; SMDWin v0.1 Beta</p>
  <span class='health-badge {healthCls}'>{health} — {L("Overall Status", "Stare Generală")}</span>
</div>

<!-- ── 1. SYSTEM OVERVIEW ── -->
<div class='grid section'>
  <div class='card'>
    <h2>💻 {L("Operating System", "Sistem de Operare")}</h2>
    {Row("Windows", summary.OsName)}
    {Row(L("Version", "Versiune"), summary.OsVersion)}
    {Row("Build", summary.OsBuild)}
    {Row(L("Architecture", "Arhitectură"), summary.Architecture)}
    {Row(L("Installed", "Instalat"), summary.InstallDate)}
    {Row(L("Uptime", "Uptime"), summary.Uptime)}
    {Row("BIOS", summary.BiosVersion)}
  </div>
  <div class='card'>
    <h2>🏭 {L("Computer", "Calculator")}</h2>
    {Row(L("Manufacturer", "Producător"), summary.Manufacturer)}
    {Row(L("Model", "Model"), summary.Model)}
    {Row("Hostname", summary.ComputerName)}
    {(summary.DisplayResolution.Length > 0 ? Row(L("Display", "Display"), $"{summary.DisplayResolution}{(summary.DisplayCount > 1 ? $" (+{summary.DisplayCount-1})" : "")}") : "")}
    {(summary.HasBattery ? Row(L("Battery", "Baterie"), $"{summary.BatteryCharge} — {summary.BatteryStatus}") : "")}
  </div>
</div>

<!-- ── 2. PROCESSOR ── -->
<div class='card section'>
  <h2>⚡ {L("Processor", "Procesor")}</h2>
  <div class='grid' style='margin-bottom:0'>
    <div>
      {Row(L("Model", "Model"), summary.Cpu)}
      {Row(L("Cores / Threads", "Nuclee / Fire"), summary.CpuCores)}
      {(summary.CpuMaxMHz.Length > 0 ? Row(L("Max Clock", "Frecvență Max"), summary.CpuMaxMHz) : "")}
    </div>
    <div>
      {(summary.CpuCache.Length > 0 ? Row(L("Cache", "Cache"), summary.CpuCache) : "")}
      {Row(L("Architecture", "Arhitectură"), summary.Architecture)}
    </div>
  </div>
</div>

<!-- ── 3. GPU ── -->
<div class='card section'>
  <h2>🎮 GPU</h2>
  {Row(L("Model", "Model"), summary.GpuName)}
  {Row("VRAM", summary.GpuVram)}
</div>

<!-- ── 4. RAM ── -->
");

                sb.Append($"<div class='card section'><h2>🧠 {L("RAM Memory Modules", "Module Memorie RAM")}</h2>");
                sb.Append($"<p style='font-size:12px;color:#64748b;margin-bottom:10px'>{L("Total", "Total")}: <strong>{summary.TotalRam}</strong></p>");
                if (ramModules.Count == 0)
                    sb.Append($"<p style='color:#64748b;font-size:12px'>{L("No RAM data available.", "Fără date RAM disponibile.")}</p>");
                else
                {
                    sb.Append($"<table><tr><th>{L("Slot","Slot")}</th><th>{L("Manufacturer","Producător")}</th><th>{L("Part Number","Part Number")}</th><th>{L("Capacity","Capacitate")}</th><th>{L("Speed","Frecvență")}</th><th>{L("Type","Tip")}</th><th>{L("Form","Form")}</th></tr>");
                    foreach (var r in ramModules)
                        sb.Append($"<tr><td>{r.BankLabel}</td><td>{r.Manufacturer}</td><td style='font-family:monospace'>{r.PartNumber}</td><td>{r.Capacity}</td><td>{r.Speed}</td><td>{r.MemoryType}</td><td>{r.FormFactor}</td></tr>");
                    sb.Append("</table>");
                }
                sb.Append("</div>");

                // ── 5. Storage ──────────────────────────────────────────────
                sb.Append($"<div class='card section'><h2>💾 {L("Storage — Disk Health", "Stocare — Sănătate Discuri")}</h2>");
                if (disks.Count == 0)
                    sb.Append($"<p style='color:#64748b;font-size:12px'>{L("No disk data available.", "Fără date despre discuri.")}</p>");
                foreach (var disk in disks)
                {
                    var barColor = disk.HealthPercent >= 80 ? "#22c55e" : disk.HealthPercent >= 50 ? "#f59e0b" : "#ef4444";
                    sb.Append($@"<div style='margin-bottom:14px'>
  <div style='display:flex;justify-content:space-between;align-items:center'>
    <strong style='font-size:13px'>{disk.Model}</strong>
    <span class='tag {(disk.HealthPercent >= 80 ? "tag-ok" : "tag-warn")}'>{disk.HealthPercent}% {L("Health", "Sănătate")}</span>
  </div>
  <div style='font-size:11px;color:#64748b;margin:3px 0'>{L("Type","Tip")}: {disk.MediaType} &nbsp;·&nbsp; {L("Size","Capacitate")}: {disk.Size} &nbsp;·&nbsp; {L("Status","Status")}: {disk.Status}</div>
  <div class='progress-bar'><div class='progress-fill' style='width:{disk.HealthPercent}%;background:{barColor}'></div></div>");
                    if (disk.Partitions.Count > 0)
                    {
                        sb.Append($"<div style='margin-top:8px'><table><tr><th>{L("Drive","Part.")}</th><th>{L("Label","Etichetă")}</th><th>FS</th><th>{L("Total","Total")}</th><th>{L("Free","Liber")}</th><th>{L("Used","Utilizat")}</th></tr>");
                        foreach (var p in disk.Partitions)
                        {
                            var uc = p.UsedPct > 90 ? "#ef4444" : p.UsedPct > 75 ? "#f59e0b" : "#16a34a";
                            sb.Append($"<tr><td><strong>{p.Letter}</strong></td><td>{p.Label}</td><td>{p.FileSystem}</td><td>{p.TotalGB:F1} GB</td><td>{p.FreeGB:F1} GB</td><td><span style='color:{uc};font-weight:700'>{p.UsedPct}%</span></td></tr>");
                        }
                        sb.Append("</table></div>");
                    }
                    sb.Append("</div><hr style='border:none;border-top:1px solid #f1f5f9;margin:10px 0'>");
                }
                sb.Append("</div>");

                // ── 6. Battery (only if laptop) ─────────────────────────────
                if (summary.HasBattery)
                {
                    sb.Append($"<div class='card section'><h2>🔋 {L("Battery", "Baterie")}</h2>");
                    double healthPct = Math.Max(0, 100 - summary.BatteryWearPct);
                    string healthColor = healthPct >= 80 ? "#16a34a" : healthPct >= 60 ? "#d97706" : "#dc2626";
                    string chargeColor = summary.BatteryChargeInt < 20 ? "#dc2626" : summary.BatteryChargeInt < 50 ? "#d97706" : "#16a34a";
                    sb.Append($@"<div class='grid' style='margin-bottom:0'>
  <div>
    {Row(L("Status","Status"), summary.BatteryStatus)}
    {Row(L("Charge","Încărcare"), summary.BatteryCharge)}
    {(summary.BatteryRuntime.Length > 0 ? Row(L("Estimated runtime","Autonomie estimată"), summary.BatteryRuntime) : "")}
    {(summary.BatteryCycles > 0 ? Row(L("Charge cycles","Cicluri de încărcare"), summary.BatteryCycles.ToString()) : "")}
  </div>
  <div style='display:flex;gap:20px;align-items:center;justify-content:center'>
    <div style='text-align:center'>
      <div style='font-size:28px;font-weight:800;color:{chargeColor}'>{summary.BatteryChargeInt}%</div>
      <div style='font-size:10px;color:#64748b'>{L("Charge","Încărcare")}</div>
      <div class='progress-bar' style='width:100px;margin-top:5px'><div class='progress-fill' style='width:{summary.BatteryChargeInt}%;background:{chargeColor}'></div></div>
    </div>
    {(summary.BatteryWearPct > 0 ? $@"<div style='text-align:center'>
      <div style='font-size:28px;font-weight:800;color:{healthColor}'>{healthPct:F0}%</div>
      <div style='font-size:10px;color:#64748b'>{L("Health","Sănătate")}</div>
      <div class='progress-bar' style='width:100px;margin-top:5px'><div class='progress-fill' style='width:{healthPct:F0}%;background:{healthColor}'></div></div>
    </div>" : "")}
  </div>
</div>");
                    sb.Append("</div>");
                }

                // ── 7. Temperatures ─────────────────────────────────────────
                if (temps.Count > 0)
                {
                    sb.Append($"<div class='card section'><h2>🌡 {L("Temperatures", "Temperaturi")}</h2>");
                    sb.Append("<table><tr><th style='width:40%'>" + L("Sensor","Senzor") + "</th><th style='width:20%;text-align:center'>" + L("Current","Curent") + "</th><th style='width:20%;text-align:center'>" + L("Min (est.)","Min (est.)") + "</th><th style='width:20%;text-align:center'>" + L("Status","Status") + "</th></tr>");
                    foreach (var t in temps.Where(x => x.Temperature > 0))
                    {
                        string color = t.Temperature >= 85 ? "#dc2626" : t.Temperature >= 70 ? "#d97706" : "#16a34a";
                        string badge = t.Temperature >= 85 ? "🔴 Hot" : t.Temperature >= 70 ? "🟡 Warm" : "🟢 OK";
                        double estMin = t.Temperature * 0.78;
                        sb.Append($"<tr><td>{t.Name}</td><td style='text-align:center;font-weight:700;color:{color}'>{t.Temperature:F0}°C</td><td style='text-align:center;color:#64748b'>{estMin:F0}°C</td><td style='text-align:center'><span class='tag' style='background:{(t.Temperature >= 85 ? "#fee2e2" : t.Temperature >= 70 ? "#fef9c3" : "#dcfce7")};color:{color}'>{badge}</span></td></tr>");
                    }
                    sb.Append("</table></div>");
                }

                // ── 8. Event stats ──────────────────────────────────────────
                sb.Append($@"<div class='grid-3 section'>
  <div class='stat-card'><div class='stat-num {(critCount > 0 ? "red" : "green")}'>{critCount}</div><div class='stat-label'>{L("Critical Errors (7d)", "Erori Critice (7 zile)")}</div></div>
  <div class='stat-card'><div class='stat-num {(warnCount > 5 ? "yellow" : "green")}'>{warnCount}</div><div class='stat-label'>{L("Warnings (7d)", "Avertismente (7 zile)")}</div></div>
  <div class='stat-card'><div class='stat-num {(crashCount > 0 ? "red" : "green")}'>{crashCount}</div><div class='stat-label'>{L("Crashes detected", "Crash-uri detectate")}</div></div>
</div>");

                // ── 9. BSOD ─────────────────────────────────────────────────
                sb.Append($"<div class='card section'><h2>💥 BSOD / {L("Crash Dumps", "Crash Dumps")}</h2>");
                if (crashCount == 0)
                    sb.Append($"<p style='color:#16a34a;font-size:12px;font-weight:600'>✔ {L("No crashes detected.", "Nu s-au detectat crash-uri.")}</p>");
                else
                {
                    sb.Append($"<table><tr><th>{L("Date","Data")}</th><th>{L("File","Fișier")}</th><th>Stop Code</th><th>{L("Module","Modul")}</th></tr>");
                    foreach (var c in crashes.Where(c => c.FileName != "Niciun crash detectat" && c.FileName != "No crash detected"))
                        sb.Append($"<tr><td>{c.CrashTime:dd.MM.yyyy HH:mm}</td><td>{c.FileName}</td><td>{c.StopCode}</td><td>{c.FaultingModule}</td></tr>");
                    sb.Append("</table>");
                }
                sb.Append("</div>");

                sb.Append($@"<div class='footer'>
  SMDWin v0.1 Beta &nbsp;·&nbsp; {now:dd.MM.yyyy HH:mm:ss} &nbsp;·&nbsp; {summary.ComputerName}
  <br>{L("Auto-generated report.", "Raport generat automat.")}
</div>
</div></body></html>");

                return sb.ToString();
            });
        }

        // ════════════════════════════════════════════════════════════════════
        // DIAGNOSIS REPORT  (full 60s+ diagnostic, modern design)
        // ════════════════════════════════════════════════════════════════════
        public async Task<string> GenerateFullDiagnosisReportAsync(
            SMDWin.Models.DiagResults d, string language = "en")
        {
            return await Task.Run(() =>
            {
                bool ro = language == "ro";
                string L(string en, string r) => ro ? r : en;
                var sb  = new StringBuilder();
                var now = DateTime.Now;

                // ── Extract values safely ────────────────────────────────────
                string compName = d.Summary.ComputerName;
                string cpu      = d.Summary.Cpu;
                string gpu      = d.Summary.GpuName;
                string os       = d.Summary.OsName;
                string ram      = d.Summary.TotalRam;
                string uptime   = d.Summary.Uptime;
                int    crashes  = d.Summary.CrashCount;
                int    critEvts = d.Summary.CriticalEvents;

                double cpuMax = d.CpuTempMax, cpuMin = d.CpuTempMin;
                double gpuMax = d.GpuTempMax, gpuMin = d.GpuTempMin;

                // Disk benchmark (null-safe via helper properties)
                bool   hasDiskBench   = d.DiskBenchmark != null;
                double diskReadMBs    = d.DiskBenchReadMBs;
                double diskWriteMBs   = d.DiskBenchWriteMBs;
                string diskBenchRating = d.DiskBenchRating;
                string diskBenchDrive  = d.DiskBenchDrive;

                bool   batPresent = d.Battery.Present;
                int    batCharge  = d.Battery.ChargePercent;
                string batStatus  = d.Battery.Status;
                double batWear    = d.Battery.WearPct;
                int    batCycles  = d.Battery.CycleCount;
                string batMfr     = d.Battery.Manufacturer;
                string batChem    = d.Battery.Chemistry;
                string batRuntime = d.Battery.RuntimeText;

                bool   speedOk  = d.Speed?.Success == true;
                double dlSpeed  = d.Speed?.DownloadMbps ?? 0;
                double ping     = d.Speed?.PingMs ?? 0;
                double jitter   = d.Speed?.JitterMs ?? 0;

                double cpuScore = d.CpuBenchScore;
                // Multi-core benchmark: typical ranges 50K-500K hash/s
                string cpuRating = cpuScore > 400_000 ? L("Excellent","Excelent")
                                 : cpuScore > 200_000 ? L("Good","Bun")
                                 : cpuScore >  80_000 ? L("Average","Mediu")
                                 : cpuScore >       0 ? L("Slow","Lent") : "—";
                string cpuRatingColor = cpuScore > 400_000 ? "#16a34a"
                                      : cpuScore > 200_000 ? "#2563eb"
                                      : cpuScore >  80_000 ? "#d97706"
                                      : cpuScore >       0 ? "#dc2626" : "#94a3b8";

                double ramReadGB  = d.RamBenchReadGBs;
                double ramWriteGB = d.RamBenchWriteGBs;

                // ── CSS ──────────────────────────────────────────────────────
                sb.Append($@"<!DOCTYPE html>
<html lang='{(ro ? "ro" : "en")}'>
<head>
<meta charset='UTF-8'>
<title>SMDWin — {L("Full Diagnosis","Diagnoză Completă")} — {compName} — {now:dd.MM.yyyy}</title>
<style>
  *{{box-sizing:border-box;margin:0;padding:0}}
  body{{font-family:'Segoe UI',Arial,sans-serif;background:#f0f4f8;color:#1a202c;padding:24px}}
  .container{{max-width:980px;margin:0 auto}}
  .header{{background:linear-gradient(135deg,#4c1d95 0%,#7c3aed 50%,#a855f7 100%);padding:28px 32px;border-radius:16px;margin-bottom:20px;position:relative;overflow:hidden}}
  .header::before{{content:'';position:absolute;top:-40px;right:-40px;width:200px;height:200px;background:rgba(255,255,255,.08);border-radius:50%}}
  .header h1{{font-size:22px;font-weight:700;color:white;margin-bottom:6px}}
  .header p{{font-size:12px;color:rgba(255,255,255,.85);margin-top:3px}}
  .grid-2{{display:grid;grid-template-columns:1fr 1fr;gap:14px;margin-bottom:14px}}
  .grid-3{{display:grid;grid-template-columns:1fr 1fr 1fr;gap:14px;margin-bottom:14px}}
  .grid-4{{display:grid;grid-template-columns:1fr 1fr 1fr 1fr;gap:12px;margin-bottom:14px}}
  .card{{background:white;border:1px solid #e2e8f0;border-radius:12px;padding:16px 18px;box-shadow:0 1px 4px rgba(0,0,0,.06)}}
  .card-title{{font-size:11px;font-weight:700;color:#6d28d9;text-transform:uppercase;letter-spacing:.05em;margin-bottom:12px;padding-bottom:8px;border-bottom:2px solid #ede9fe}}
  .stat{{text-align:center;padding:12px 8px}}
  .stat-val{{font-size:22px;font-weight:800;line-height:1.1}}
  .stat-sub{{font-size:11px;color:#64748b;margin-top:3px}}
  .stat-unit{{font-size:11px;font-weight:500;color:#64748b}}
  .row{{display:flex;justify-content:space-between;padding:4px 0;border-bottom:1px solid #f1f5f9;font-size:11px}}
  .row:last-child{{border-bottom:none}}
  .lbl{{color:#64748b}}.val{{color:#0f172a;font-weight:600}}
  .green{{color:#16a34a}}.yellow{{color:#d97706}}.red{{color:#dc2626}}.blue{{color:#2563eb}}.purple{{color:#7c3aed}}
  .tag{{display:inline-block;padding:2px 8px;border-radius:8px;font-size:10px;font-weight:700}}
  .tag-ok{{background:#dcfce7;color:#166534}}.tag-warn{{background:#fef9c3;color:#854d0e}}.tag-err{{background:#fee2e2;color:#991b1b}}
  .temp-chart{{margin-top:8px}}
  .temp-bar-row{{display:flex;align-items:center;gap:8px;margin-bottom:6px;font-size:11px}}
  .temp-bar-label{{width:90px;color:#64748b;flex-shrink:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}}
  .temp-bar-track{{flex:1;background:#e5e7eb;border-radius:4px;height:12px;position:relative}}
  .temp-bar-min{{position:absolute;height:100%;border-radius:4px;top:0}}
  .temp-bar-fill{{position:absolute;height:100%;border-radius:4px;top:0}}
  .pbar{{background:#e5e7eb;border-radius:4px;height:6px;margin-top:4px}}
  .pfill{{height:6px;border-radius:4px}}
  .net-detail{{font-size:10px;color:#64748b;margin-top:2px}}
  table{{width:100%;border-collapse:collapse;font-size:11px}}
  th{{background:#ede9fe;color:#4c1d95;padding:6px 10px;text-align:left;font-weight:600;font-size:10px;text-transform:uppercase;letter-spacing:.04em}}
  td{{padding:5px 10px;border-bottom:1px solid #f1f5f9;color:#1a202c}}
  tr:last-child td{{border-bottom:none}}
  .footer{{text-align:center;color:#94a3b8;font-size:10px;margin-top:24px;padding-top:14px;border-top:1px solid #e2e8f0}}
  .net-adapter-box{{background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:8px 10px;margin-bottom:6px}}
  @media print{{
    body{{background:white;padding:0}}
    .card{{box-shadow:none}}
  }}
</style>
</head>
<body>
<div class='container'>

<div class='header'>
  <h1>🔬 {L("Full System Diagnosis", "Diagnoză Completă Sistem")}</h1>
  <p>{compName} &nbsp;·&nbsp; {cpu} &nbsp;·&nbsp; {os}</p>
  <p>{L("Generated","Generat")}: {now:dd MMMM yyyy, HH:mm:ss} &nbsp;·&nbsp; {L("Uptime","Uptime")}: {uptime} &nbsp;·&nbsp; RAM: {ram}</p>
  {(d.IsExtended ? $"<span style='display:inline-block;margin-top:6px;padding:3px 14px;background:rgba(167,139,250,.25);border:1px solid #7c3aed;border-radius:12px;font-size:11px;font-weight:700;color:#c4b5fd'>🔬 {L("Extended Diagnostic — 3 min stress + benchmark","Diagnostic Extins — 3 min stres + benchmark")}</span>" : $"<span style='display:inline-block;margin-top:6px;padding:3px 14px;background:rgba(34,197,94,.15);border:1px solid #16a34a;border-radius:12px;font-size:11px;font-weight:700;color:#4ade80'>⚡ {L("Quick Diagnostic","Diagnostic Rapid")}</span>")}
</div>

");
                // ── CPU Benchmark ─────────────────────────────────────────────
                sb.Append($@"<div class='card' style='margin-bottom:14px'>
<div class='card-title'>⚡ {L("CPU Benchmark","Benchmark CPU")} — {cpu}</div>
<div class='grid-4' style='margin-bottom:0'>
  <div class='stat'>
    <div class='stat-val' style='color:{cpuRatingColor}'>{(cpuScore > 0 ? $"{cpuScore / 1000:N0}K" : "—")}</div>
    <div class='stat-sub'>{L("SHA-256 hash/s (÷1K)","SHA-256 hash/s (÷1K)")}</div>
  </div>
  <div class='stat'>
    <div class='stat-val purple'>{(cpuScore > 0 ? $"{cpuScore:N0}" : "—")}</div>
    <div class='stat-sub'>{L("Raw hash/s","Hash/s brut")}</div>
  </div>
  <div class='stat'>
    <div class='stat-val' style='color:{cpuRatingColor}'>{cpuRating}</div>
    <div class='stat-sub'>{L("Rating","Rating")}</div>
  </div>
  <div class='stat'>
    <div class='stat-val blue'>{d.Summary.CpuCores}</div>
    <div class='stat-sub'>{L("Cores / Threads","Nuclee / Fire")}</div>
  </div>
</div>
{(cpuScore > 0 ? PerformanceBar(cpuScore, CpuScaleMin, CpuScaleMax, "Celeron N-series", "i9-14th Gen", "hash/s", language) : "")}
</div>
");

                // ── Temperatures (bar chart only, no redundant big numbers) ──────
                // Title changes based on mode: Extended = stress temps, Quick = idle temps
                string tempTitle = d.TempsFromStress
                    ? L("🌡 Temperature — Under Full Load (3 min stress)", "🌡 Temperaturi — Sub Sarcină Maximă (3 min stres)")
                    : L("🌡 Temperature — Idle / Benchmark", "🌡 Temperaturi — Idle / Benchmark");
                sb.Append($"<div class='card' style='margin-bottom:14px'><div class='card-title'>{tempTitle}</div>");
                // Disclaimer only for Quick mode (2-min idle temps, not meaningful for thermal analysis)
                if (!d.TempsFromStress)
                    sb.Append($"<p style=\"font-size:10px;color:#94a3b8;background:#1e293b;border:1px solid #334155;border-radius:6px;padding:6px 10px;margin-bottom:10px\">ℹ️ {L("Temperatures recorded during a ~2 min benchmark run. For accurate thermal analysis use Extended Diagnostic (5 min).","Temperaturi înregistrate în timpul unui benchmark de ~2 min. Pentru analiză termică exactă folosiți Diagnostic Extins (5 min).")} </p>");
                else
                    sb.Append($"<p style=\"font-size:10px;color:#4ade80;background:rgba(22,163,74,.1);border:1px solid #16a34a;border-radius:6px;padding:6px 10px;margin-bottom:10px\">✔ {L("Temperatures captured under sustained CPU+GPU load for 3 minutes — values reflect real-world thermal performance.","Temperaturi capturate sub sarcină maximă CPU+GPU timp de 3 minute — valorile reflectă performanța termică reală.")} </p>");

                // Per-sensor bar chart — range shown inline, no big stat block
                if (d.AllTemps.Count > 0)
                {
                    sb.Append("<div class='temp-chart' style='margin-top:4px'>");
                    var cpuSensors = d.AllTemps.Where(t => t.Temperature > 0 &&
                        (t.Name.Contains("CPU") || t.Name.Contains("Package") || t.Name.Contains("Tdie"))).Take(3);
                    var gpuSensors = d.AllTemps.Where(t => t.Temperature > 0 && t.Name.Contains("GPU")).Take(2);
                    var otherSensors = d.AllTemps.Where(t => t.Temperature > 0 &&
                        !t.Name.Contains("CPU") && !t.Name.Contains("Package") &&
                        !t.Name.Contains("Tdie") && !t.Name.Contains("GPU")).Take(4);

                    foreach (var t in cpuSensors.Concat(gpuSensors).Concat(otherSensors))
                    {
                        bool isCpu = t.Name.Contains("CPU") || t.Name.Contains("Package") || t.Name.Contains("Tdie");
                        bool isGpu = t.Name.Contains("GPU");
                        double tmax = isCpu && cpuMax > 0 ? cpuMax : isGpu && gpuMax > 0 ? gpuMax : t.Temperature;
                        double tmin = isCpu && cpuMin > 0 ? cpuMin : isGpu && gpuMin > 0 ? gpuMin : t.Temperature * 0.80;
                        double pctMin = Math.Min(100, tmin / 110.0 * 100);
                        double pctMax = Math.Min(100, tmax / 110.0 * 100);
                        string barColor = tmax >= 85 ? "#dc2626" : tmax >= 70 ? "#d97706" : "#16a34a";
                        // Range label: "42° – 78°C" — min first, max second, font-size 12px
                        sb.Append($@"<div class='temp-bar-row'>
  <div class='temp-bar-label' title='{t.Name}' style='font-size:12px;color:#374151'>{t.Name}</div>
  <div class='temp-bar-track'>
    <div class='temp-bar-min' style='width:{pctMin:F0}%;background:#bfdbfe'></div>
    <div class='temp-bar-fill' style='left:{pctMin:F0}%;width:{(pctMax - pctMin):F0}%;background:{barColor}'></div>
  </div>
  <div style='width:90px;text-align:right;flex-shrink:0;font-size:12px;color:#475569'>{tmin:F0}° <span style='color:#94a3b8'>–</span> <span style='color:{barColor};font-weight:700;font-size:13px'>{tmax:F0}°C</span></div>
</div>");
                    }
                    sb.Append("</div>");
                }

                // Throttle section
                if (d.CpuThrottleDetected)
                    sb.Append($@"<div style='margin-top:10px;padding:10px 14px;background:#7f1d1d;border:1px solid #ef4444;border-radius:8px;color:#fca5a5'>
  <span style='font-weight:700'>⚠ CPU Throttling {L("detected","detectat")}!</span>
  {L($"The processor reduced its frequency in {d.CpuThrottlePct:F0}% of the stress test time ({d.CpuThrottleCount} samples).",
     $"Procesorul și-a redus frecvența în {d.CpuThrottlePct:F0}% din timpul testului ({d.CpuThrottleCount} eșantioane).")}
  {L("Possible causes: thermal limits, power limits, battery saver mode.",
     "Cauze posibile: limită termică, limită de putere, mod economisire baterie.")}
</div>");
                else if (d.CpuThrottleCount == 0 && cpuMax > 0)
                    sb.Append($"<div style='margin-top:10px;font-size:11px;color:#4ade80'>✔ {L("No CPU throttling detected during stress test.","Nu s-a detectat throttling CPU în timpul testului.")}</div>");

                sb.Append("</div>");

                // ── Disk Health + Speed ───────────────────────────────────────
                sb.Append($"<div class='card' style='margin-bottom:14px'><div class='card-title'>💾 {L("Disk Health & Speed","Sănătate & Viteză Disc")}</div>");
                if (d.Disks.Count == 0)
                    sb.Append($"<p style='color:#64748b;font-size:11px'>{L("No disk data.","Fără date disc.")}</p>");
                foreach (var disk in d.Disks)
                {
                    var barColor = disk.HealthPercent >= 80 ? "#4ade80" : disk.HealthPercent >= 50 ? "#fbbf24" : "#f87171";
                    sb.Append($@"<div style='margin-bottom:12px'>
  <div style='display:flex;justify-content:space-between;align-items:center;margin-bottom:3px'>
    <span style='font-size:12px;font-weight:600;color:#111827'>{disk.Model}</span>
    <span class='tag {(disk.HealthPercent >= 80 ? "tag-ok" : disk.HealthPercent >= 50 ? "tag-warn" : "tag-err")}'>{disk.HealthPercent}% {L("health","sănătate")}</span>
  </div>
  <div style='font-size:10px;color:#374151;margin-bottom:4px'>{L("Type","Tip")}: {disk.MediaType} · {L("Size","Dim.")}: {disk.Size} · {disk.Status}</div>
  <div class='pbar'><div class='pfill' style='width:{disk.HealthPercent}%;background:{barColor}'></div></div>
</div>");
                }
                if (d.DiskBenchmark != null)
                {
                    var bm = d.DiskBenchmark;
                    sb.Append($@"<div style='margin-top:10px;padding-top:10px;border-top:1px solid #e2e8f0'>
  <div style='font-size:10px;color:#7c3aed;font-weight:700;margin-bottom:8px;text-transform:uppercase;letter-spacing:.05em'>{L("Benchmark Results","Rezultate Benchmark")} — {bm.DriveLetter}</div>
  <div class='grid-2' style='margin-bottom:0;gap:10px'>
    <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:10px;text-align:center'>
      <div style='font-size:22px;font-weight:800;color:#16a34a'>{bm.SeqReadMBs:F0} <span style='font-size:12px;color:#64748b'>MB/s</span></div>
      <div style='font-size:10px;color:#64748b;margin-top:2px'>{L("Sequential Read","Citire Secvențială")}</div>
      {PerformanceBar(bm.SeqReadMBs, DiskReadScaleMin, DiskReadScaleMax, "HDD 5400rpm", "NVMe Gen4", "MB/s", language)}
    </div>
    <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:10px;text-align:center'>
      <div style='font-size:22px;font-weight:800;color:#2563eb'>{bm.SeqWriteMBs:F0} <span style='font-size:12px;color:#64748b'>MB/s</span></div>
      <div style='font-size:10px;color:#64748b;margin-top:2px'>{L("Sequential Write","Scriere Secvențială")}</div>
      {PerformanceBar(bm.SeqWriteMBs, DiskWriteScaleMin, DiskWriteScaleMax, "HDD 5400rpm", "NVMe Gen4", "MB/s", language)}
    </div>
  </div>
  <div style='text-align:center;margin-top:8px;font-size:11px;color:#64748b'>{L("Rating","Rating")}: <span style='color:{bm.RatingColor};font-weight:700'>{bm.Rating}</span></div>
</div>");
                }
                sb.Append("</div>");

                // ── RAM ───────────────────────────────────────────────────────
                sb.Append($"<div class='card' style='margin-bottom:14px'><div class='card-title'>🧠 {L("RAM Modules","Module RAM")}</div>");
                if (d.RamModules.Count > 0)
                {
                    sb.Append($"<table style='margin-bottom:10px'><tr><th>{L("Slot","Slot")}</th><th>{L("Manufacturer","Prod.")}</th><th>{L("Capacity","Cap.")}</th><th>{L("Speed","Viteză")}</th><th>{L("Type","Tip")}</th></tr>");
                    foreach (var r in d.RamModules)
                        sb.Append($"<tr><td>{r.BankLabel}</td><td>{r.Manufacturer}</td><td>{r.Capacity}</td><td>{r.Speed}</td><td>{r.MemoryType}</td></tr>");
                    sb.Append("</table>");
                }
                if (ramReadGB > 0)
                {
                    sb.Append($@"<div style='padding-top:10px;border-top:1px solid #e2e8f0'>
  <div style='font-size:10px;color:#7c3aed;font-weight:700;margin-bottom:8px;text-transform:uppercase;letter-spacing:.05em'>{L("RAM Bandwidth Benchmark","Benchmark Lățime de Bandă RAM")}</div>
  <div class='grid-2' style='margin-bottom:0;gap:10px'>
    <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:10px;text-align:center'>
      <div style='font-size:22px;font-weight:800;color:#4ade80'>{ramReadGB:F1} <span style='font-size:11px;color:#64748b'>GB/s</span></div>
      <div style='font-size:10px;color:#64748b;margin-top:2px'>{L("Read Bandwidth","Lățime Citire")}</div>
      {PerformanceBar(ramReadGB, RamReadScaleMin, RamReadScaleMax, "DDR3-1333", "DDR5-6000", "GB/s", language)}
    </div>
    <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:10px;text-align:center'>
      <div style='font-size:22px;font-weight:800;color:#60a5fa'>{ramWriteGB:F1} <span style='font-size:11px;color:#64748b'>GB/s</span></div>
      <div style='font-size:10px;color:#64748b;margin-top:2px'>{L("Write Bandwidth","Lățime Scriere")}</div>
      {PerformanceBar(ramWriteGB, RamWriteScaleMin, RamWriteScaleMax, "DDR3-1333", "DDR5-6000", "GB/s", language)}
    </div>
  </div>
</div>");
                }
                sb.Append("</div>");

                // ── Battery ───────────────────────────────────────────────────
                sb.Append($"<div class='card' style='margin-bottom:14px'><div class='card-title'>🔋 {L("Battery","Baterie")}</div>");
                if (!batPresent)
                    sb.Append($"<p style='color:#64748b;font-size:11px'>{L("Battery not detected (desktop or missing driver).","Baterie nedepistată (desktop sau driver lipsă).")}</p>");
                else
                {
                    double healthPct2 = batWear > 0 ? Math.Max(0, 100 - batWear) : 0;
                    string healthColor2 = healthPct2 >= 80 ? "#4ade80" : healthPct2 >= 60 ? "#fbbf24" : "#f87171";
                    string chargeColor = batCharge < 20 ? "#f87171" : batCharge < 50 ? "#fbbf24" : "#4ade80";
                    sb.Append($@"<div class='grid-2' style='margin-bottom:0'>
  <div>
    <div class='row'><span class='lbl'>{L("Status","Status")}</span><span class='val'>{batStatus}</span></div>
    {(batRuntime.Length > 0 && batRuntime != "—" ? $"<div class='row'><span class='lbl'>{L("Runtime","Autonomie")}</span><span class='val'>{batRuntime}</span></div>" : "")}
    {(batCycles > 0 ? $"<div class='row'><span class='lbl'>{L("Charge cycles","Cicluri")}</span><span class='val'>{batCycles}</span></div>" : "")}
  </div>
  <div style='display:flex;flex-direction:row;align-items:center;justify-content:center;gap:20px'>
    <div style='text-align:center'>
      <div style='font-size:32px;font-weight:800;color:{chargeColor}'>{batCharge}%</div>
      <div style='font-size:10px;color:#94a3b8'>{L("Charge","Încărcare")}</div>
      <div class='pbar' style='width:100px;margin-top:6px'><div class='pfill' style='width:{batCharge}%;background:{chargeColor}'></div></div>
    </div>
    {(healthPct2 > 0 ? $@"<div style='text-align:center'>
      <div style='font-size:32px;font-weight:800;color:{healthColor2}'>{healthPct2:F0}%</div>
      <div style='font-size:10px;color:#94a3b8'>{L("Health","Sănătate")}</div>
      <div class='pbar' style='width:100px;margin-top:6px'><div class='pfill' style='width:{healthPct2:F0}%;background:{healthColor2}'></div></div>
    </div>" : "")}
  </div>
</div>");
                }
                sb.Append("</div>");

                // ── Network ───────────────────────────────────────────────────
                sb.Append($"<div class='card' style='margin-bottom:14px'><div class='card-title'>🌐 {L("Network","Rețea")}</div>");
                sb.Append($"<div class='grid-2' style='margin-bottom:0'>");

                // Speed
                sb.Append($@"<div>
  <div style='font-size:10px;color:#7c3aed;font-weight:700;margin-bottom:8px;text-transform:uppercase;letter-spacing:.05em'>{L("Internet Speed","Viteză Internet")}</div>
  {(!d.IsExtended ? $"<p style='font-size:10px;color:#94a3b8;background:#f8fafc;border:1px solid #e2e8f0;border-radius:6px;padding:5px 8px;margin-bottom:8px'>ℹ️ {L("Network speed is not a hardware performance metric — it reflects your ISP connection, not the PC capabilities.","Viteza rețelei nu este un indicator de performanță hardware — reflectă conexiunea ISP, nu capacitățile PC-ului.")}</p>" : "")}");
                if (!speedOk)
                    sb.Append($"<p style='color:#64748b;font-size:11px'>{L("Speed test unavailable.","Speedtest indisponibil.")}</p>");
                else
                    sb.Append($@"<div style='display:flex;gap:8px;flex-wrap:wrap'>
    <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:8px 12px;text-align:center;flex:1'>
      <div style='font-size:20px;font-weight:800;color:#4ade80'>{dlSpeed:F1}</div>
      <div style='font-size:10px;color:#64748b'>Mbps ↓</div>
    </div>
    <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:8px 12px;text-align:center;flex:1'>
      <div style='font-size:20px;font-weight:800;color:#60a5fa'>{ping:F0}</div>
      <div style='font-size:10px;color:#64748b'>ms Ping</div>
    </div>
    <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:8px 12px;text-align:center;flex:1'>
      <div style='font-size:20px;font-weight:800;color:#fbbf24'>{jitter:F0}</div>
      <div style='font-size:10px;color:#64748b'>ms Jitter</div>
    </div>
  </div>");
                sb.Append("</div>");

                // Adapters
                sb.Append($@"<div>
  <div style='font-size:10px;color:#7c3aed;font-weight:700;margin-bottom:8px;text-transform:uppercase;letter-spacing:.05em'>{L("Network Adapters","Adaptoare Rețea")}</div>");
                if (d.NetworkAdapters.Count == 0)
                    sb.Append($"<p style='color:#64748b;font-size:11px'>{L("No adapter data.","Fără date adaptoare.")}</p>");
                foreach (var net in d.NetworkAdapters.Take(3))
                {
                    string statusDot = net.Status == "Up" ? "🟢" : "⚫";
                    sb.Append($@"<div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:8px 10px;margin-bottom:6px'>
    <div style='font-size:11px;font-weight:600;color:#1a202c'>{statusDot} {net.Name}</div>
    <div class='net-detail'>IP: {net.IpAddress} &nbsp;·&nbsp; MAC: {net.MacAddress}</div>
    {(string.IsNullOrEmpty(net.Gateway) ? "" : $"<div class='net-detail'>GW: {net.Gateway} &nbsp;·&nbsp; DNS: {net.Dns}</div>")}
  </div>");
                }
                sb.Append("</div></div></div>");

                // ── BSOD / Crashes ────────────────────────────────────────────
                sb.Append($"<div class='card' style='margin-bottom:14px'><div class='card-title'>💥 BSOD / {L("Crash Dumps","Crash Dumps")}</div>");
                var diagCrashes = d.Crashes.Where(c => c.FileName != "Niciun crash detectat" && c.FileName != "No crash detected").ToList();
                if (diagCrashes.Count == 0)
                    sb.Append($"<p style='color:#16a34a;font-size:12px;font-weight:600'>✔ {L("No crashes detected.","Nu s-au detectat crash-uri.")}</p>");
                else
                {
                    sb.Append($"<table><tr><th>{L("Date","Data")}</th><th>{L("File","Fișier")}</th><th>Stop Code</th><th>{L("Module","Modul")}</th></tr>");
                    foreach (var c in diagCrashes)
                        sb.Append($"<tr><td>{c.CrashTime:dd.MM.yyyy HH:mm}</td><td>{c.FileName}</td><td>{c.StopCode}</td><td>{c.FaultingModule}</td></tr>");
                    sb.Append("</table>");
                }
                sb.Append("</div>");

                sb.Append($@"<div class='footer'>
  SMDWin v0.1 Beta — {L("Full Diagnosis","Diagnoză Completă")} &nbsp;·&nbsp; {now:dd.MM.yyyy HH:mm:ss} &nbsp;·&nbsp; {compName}
</div>
</div></body></html>");

                return sb.ToString();
            });
        }

        // ════════════════════════════════════════════════════════════════════
        // PERFORMANCE REFERENCE SCALES  (based on real-world benchmarks)
        // Left = weakest real system, Right = top-tier current system
        // Scale chosen so mid-range hardware lands in 40-65% zone
        // ════════════════════════════════════════════════════════════════════

        // CPU: SHA-256 hash/s (WinDiag single-threaded benchmark)
        // Celeron N4020 ~12K | Pentium ~30K | i5-8th ~120K | i7-8th ~160K
        // i5-12th ~220K | i9-13th ~350K | i9-14th ~420K
        private const double CpuScaleMin = 10_000;
        private const double CpuScaleMax = 420_000;

        // RAM Read bandwidth (GB/s, managed Buffer.BlockCopy)
        // DDR3-1333 single ~6 | DDR3-1600 ~9 | DDR4-2666 ~18 | DDR4-3200 ~25
        // DDR5-4800 ~38 | DDR5-6000 ~50
        private const double RamReadScaleMin = 5.0;
        private const double RamReadScaleMax = 52.0;

        // RAM Write bandwidth
        private const double RamWriteScaleMin = 4.0;
        private const double RamWriteScaleMax = 48.0;

        // Disk Sequential Read (MB/s) — realistic consumer range
        // HDD 5400rpm ~80 | HDD 7200rpm ~150 | SATA SSD ~550
        // NVMe Gen3 ~3500 | NVMe Gen4 ~7000 | NVMe Gen5 ~12000
        // Scale: left=HDD, right=NVMe Gen4 (Gen5 is niche, would push SATA too far left)
        private const double DiskReadScaleMin = 70.0;
        private const double DiskReadScaleMax = 7_000.0;

        // Disk Sequential Write (MB/s)
        private const double DiskWriteScaleMin = 60.0;
        private const double DiskWriteScaleMax = 6_500.0;

        /// <summary>
        /// Renders a horizontal performance bar showing where the value sits
        /// on a log-scale spectrum from weakest to top-tier hardware.
        /// </summary>
        private static string PerformanceBar(
            double value, double scaleMin, double scaleMax,
            string labelLeft, string labelRight,
            string unit, string lang = "en")
        {
            if (value <= 0) return "";
            bool ro = lang == "ro";

            // Log scale so HDD/DDR3 end has visible resolution
            double logVal  = Math.Log(Math.Max(value,   scaleMin), 10);
            double logMin  = Math.Log(scaleMin,          10);
            double logMax  = Math.Log(scaleMax,          10);
            double pct     = Math.Clamp((logVal - logMin) / (logMax - logMin), 0, 1);
            double pctRnd  = Math.Round(pct * 100, 1);

            // Color: red <25%, orange 25-50%, blue 50-80%, green >80%
            string fill = pctRnd >= 80 ? "#16a34a"
                        : pctRnd >= 50 ? "#2563eb"
                        : pctRnd >= 25 ? "#d97706"
                        :                "#dc2626";

            string label = ro ? "Performanță relativă" : "Relative performance";

            return $@"<div style='margin:10px 0 4px'>
  <div style='display:flex;justify-content:space-between;font-size:9px;color:#94a3b8;margin-bottom:3px'>
    <span>◀ {labelLeft}</span>
    <span style='color:#475569;font-weight:600'>{label}: {pctRnd:F0}%</span>
    <span>{labelRight} ▶</span>
  </div>
  <div style='background:#e5e7eb;border-radius:6px;height:14px;position:relative;overflow:visible'>
    <div style='width:{pctRnd:F1}%;background:linear-gradient(90deg,{fill}99,{fill});height:14px;border-radius:6px;transition:width .3s'></div>
    <div style='position:absolute;top:-2px;left:calc({pctRnd:F1}% - 7px);width:14px;height:18px;background:{fill};border:2px solid white;border-radius:4px;box-shadow:0 1px 4px rgba(0,0,0,.25)'></div>
  </div>
  <div style='font-size:9px;color:#94a3b8;margin-top:3px;text-align:right'>{value:N0} {unit}</div>
</div>";
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private static string TempCls(double t) =>
            t >= 85 ? "red" : t >= 70 ? "yellow" : t > 0 ? "green" : "";
        private static string F1OrNA(double t) =>
            t > 0 ? $"{t:F1}" : "N/A";

        private static string RowH(string label, string value) =>
            $"<div class='row'><span class='lbl'>{label}</span><span class='val'>{System.Web.HttpUtility.HtmlEncode(value)}</span></div>";

        public async Task SaveReportAsync(string htmlContent, string path) =>
            await Task.Run(() => File.WriteAllText(path, htmlContent, Encoding.UTF8));
    }
}
