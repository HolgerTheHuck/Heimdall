using System;

namespace Heimdall.Ingest;

/// <summary>
/// Konfiguration des Ingest-Buffers.
/// </summary>
public sealed class IngestOptions
{
    /// <summary>Maximale Anzahl gepufferter Items pro Signal. Default 20_000.</summary>
    public int MaxQueueItems { get; set; } = 20_000;

    /// <summary>Max. Spans pro Batch-Flush. Default 256.</summary>
    public int BatchSpans { get; set; } = 256;

    /// <summary>Max. Logs pro Batch-Flush. Default 1024.</summary>
    public int BatchLogs { get; set; } = 1024;

    /// <summary>Max. Metrik-Punkte pro Batch-Flush. Default 1024.</summary>
    public int BatchMetrics { get; set; } = 1024;

    /// <summary>Verhalten bei vollem Puffer. Default: aelteste Items verwerfen.</summary>
    public IngestDropPolicy DropPolicy { get; set; } = IngestDropPolicy.DropOldest;
}

/// <summary>Verhalten bei vollem Ingest-Puffer.</summary>
public enum IngestDropPolicy
{
    /// <summary>Aelteste Items verwerfen (Ring-Buffer-Charakter).</summary>
    DropOldest,
    /// <summary>Neue Items verwerfen (aktuelle bleiben erhalten).</summary>
    DropNewest
}