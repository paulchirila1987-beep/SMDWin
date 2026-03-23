using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class EventLogService
    {
        private static readonly string[] WatchedLogs = { "System", "Application" };

        public async Task<List<EventLogEntry>> GetEventsAsync(
            DateTime from, DateTime to,
            string levelFilter = "All",
            string searchText = "",
            System.Threading.CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var results = new List<EventLogEntry>();
                // searchText used in C# filter only — never interpolated into XPath (injection prevention)
                string safeSearch = searchText?.Trim() ?? "";

                foreach (var logName in WatchedLogs)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var query = BuildQuery(logName, from, to, levelFilter);
                        using var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, query));

                        EventRecord? record;
                        int count = 0;
                        while (!ct.IsCancellationRequested &&
                               (record = reader.ReadEvent()) != null && count < 1000)
                        {
                            using (record)
                            {
                                try
                                {
                                    var level   = GetLevelName(record.Level ?? 0);
                                    var message = "";
                                    try   { message = record.FormatDescription() ?? ""; }
                                    catch { message = record.Properties.Count > 0
                                                ? record.Properties[0].Value?.ToString() ?? "" : ""; }

                                    // Truncate early to avoid allocating huge strings for search
                                    if (message.Length > 500) message = message[..500] + "…";

                                    // C# filter — no XPath injection risk
                                    if (!string.IsNullOrEmpty(safeSearch) &&
                                        !message.Contains(safeSearch, StringComparison.OrdinalIgnoreCase) &&
                                        !(record.ProviderName?.Contains(safeSearch, StringComparison.OrdinalIgnoreCase) ?? false))
                                        continue;

                                    results.Add(new EventLogEntry
                                    {
                                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                                        Level       = level,
                                        Source      = record.ProviderName ?? "",
                                        EventId     = record.Id,
                                        Message     = message.Length > 300 ? message[..300] + "…" : message,
                                        LogName     = logName
                                    });
                                    count++;
                                }
                                catch { /* skip malformed records */ }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add(new EventLogEntry
                        {
                            TimeCreated = DateTime.Now,
                            Level       = "Error",
                            Source      = "SMDWin",
                            Message     = $"Nu s-a putut citi log-ul '{logName}': {ex.Message}",
                            LogName     = logName
                        });
                    }
                }

                results.Sort((a, b) => b.TimeCreated.CompareTo(a.TimeCreated));
                return results;
            }, ct);
        }

        private static string BuildQuery(string logName, DateTime from, DateTime to, string levelFilter)
        {
            var fromStr = from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var toStr = to.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            var levelXml = levelFilter switch
            {
                "Critical" => " and *[System[Level=1]]",
                "Error"    => " and *[System[Level=2]]",
                "Warning"  => " and *[System[Level=3]]",
                "Errors & Warnings" => " and *[System[Level<=3]]",
                _ => ""
            };

            return $"*[System[TimeCreated[@SystemTime>='{fromStr}' and @SystemTime<='{toStr}']{levelXml}]]";
        }

        private static string GetLevelName(byte level) => level switch
        {
            1 => "Critical",
            2 => "Error",
            3 => "Warning",
            4 => "Information",
            5 => "Verbose",
            _ => "Unknown"
        };
    }
}
