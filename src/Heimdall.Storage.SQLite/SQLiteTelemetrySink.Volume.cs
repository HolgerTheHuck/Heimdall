using System;
using System.Collections.Generic;
using System.Text;
using Heimdall;
using Microsoft.Data.Sqlite;

namespace Heimdall.Storage.SQLite;

// ---------------------------------------------------------------------------
// Signal-Volumen je Zeit-Bucket (IHeimdallQuery.ListSignalVolume, partial der
// Sink) fuer das Signal-Band der Übersichts-Seite: Spans/Logs/Metrik-Punkte
// pro Minute (oder anderem Bucket) im Fenster. Je Tabelle EINE GROUP-Bucket-
// Abfrage ueber den Zeit-Index (idx_heim_spans_start/idx_heim_logs_ts/
// idx_heim_metrics_ts) — Range-Scan + sequentieller Zaehl-Pass, kein Full
// Scan. Merge in C#: nur Buckets mit Vorkommen (sparse), aufsteigend; die
// UI fuellt Luecken selbst mit 0 auf.
// ---------------------------------------------------------------------------

public sealed partial class SQLiteTelemetrySink
{
    public IReadOnlyList<SignalVolumePoint> ListSignalVolume(
        long fromUnixNano, long toUnixNano, long bucketUnixNano)
    {
        if (bucketUnixNano <= 0 || fromUnixNano > toUnixNano)
            return Array.Empty<SignalVolumePoint>();

        // slot 0 = Spans, 1 = Logs, 2 = Metrik-Punkte.
        var map = new Dictionary<long, long[]>();
        AppendBucketCounts(map, 0, "heim_spans", "start_unix_nano", fromUnixNano, toUnixNano, bucketUnixNano);
        AppendBucketCounts(map, 1, "heim_logs", "ts_unix_nano", fromUnixNano, toUnixNano, bucketUnixNano);
        AppendBucketCounts(map, 2, "heim_metrics", "ts_unix_nano", fromUnixNano, toUnixNano, bucketUnixNano);
        if (map.Count == 0) return Array.Empty<SignalVolumePoint>();

        var buckets = new List<long>(map.Keys);
        buckets.Sort();
        var result = new List<SignalVolumePoint>(buckets.Count);
        foreach (var b in buckets)
        {
            var c = map[b];
            result.Add(new SignalVolumePoint(b, c[0], c[1], c[2]));
        }
        return result;
    }

    /// <summary>Eine GROUP-Bucket-Abfrage: <c>(ts / bucket) * bucket</c> ist
    /// Ganzzahl-Division (beide INTEGER), ergibt den Bucket-Anfang; gezaehlt
    /// werden die Zeilen im Fenster [from, to] inklusive. Das Ergebnis wird
    /// in <paramref name="map"/> unter Slot <paramref name="slot"/> gemerged
    /// (Buckets koennen in mehreren Signalen gleichzeitig Vorkommen).</summary>
    private void AppendBucketCounts(Dictionary<long, long[]> map, int slot,
        string table, string tsCol, long fromUnixNano, long toUnixNano, long bucketUnixNano)
    {
        var sb = SqlBuilder();
        sb.Append("SELECT (").Append(tsCol).Append(" / @b) * @b, COUNT(*) FROM ").Append(table)
          .Append(" WHERE ").Append(tsCol).Append(" >= @from AND ").Append(tsCol).Append(" <= @to")
          .Append(" GROUP BY 1");
        var ps = new List<SqliteParameter>
        {
            Param("@b", bucketUnixNano),
            Param("@from", fromUnixNano),
            Param("@to", toUnixNano),
        };

        using (var rc = OpenReadConnection())
        using (var cmd = BuildRead(rc, sb.ToString(), ps))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var b = r.GetInt64(0);
                if (!map.TryGetValue(b, out var counts))
                    map[b] = counts = new long[3];
                counts[slot] = r.GetInt64(1);
            }
        }
    }
}