using Heimdall;
using Heimdall.Cli.Mcp;
using Heimdall.Storage.SQLite;

namespace Heimdall.Cli;

// ---------------------------------------------------------------------------
// heimdall mcp — Heimdall als MCP-Server (stdio, JSON-RPC 2.0) fuer AI-Sessions,
// die den Telemetrie-Bestand als Diagnose-Element lesen wollen. Reiner Lesezugriff:
// legt NIE eine DB an und mutiert keine Bestandsdaten (kein Sweep, kein VACUUM,
// kein auto_vacuum-Umbau). WAL erlaubt das Lesen neben einem laufenden Host.
// stdout traegt ausschliesslich das MCP-Protokoll; Diagnosen gehen auf stderr.
// ---------------------------------------------------------------------------

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? db = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "mcp":
                    break;
                case "--db" when i + 1 < args.Length:
                    db = args[++i];
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unbekanntes Argument: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        db ??= Environment.GetEnvironmentVariable("HEIMDALL_DB") ?? "heimdall-otel.db";

        if (!File.Exists(db))
        {
            Console.Error.WriteLine($"heimdall mcp: Datenbank nicht gefunden: {db}");
            Console.Error.WriteLine("Zeige auf eine bestehende Heimdall-DB (--db <pfad> oder HEIMDALL_DB).");
            return 2;
        }

        // Side-Effekt-minimierende Optionen: nur lesen, nichts aufraeumen/umbauen.
        var opts = new SQLiteTelemetryOptions
        {
            DataPath = db,
            RetentionDays = 0,          // unbegrenzt — das Tool loescht nichts
            RetentionSweepMinutes = 0,  // kein Sweep-Timer
            WalMode = true,             // lesend neben laufendem Host (no-op wenn schon WAL)
            AutoVacuum = false,         // kein auto_vacuum-Umbau
            VacuumMigrateLegacy = false, // keine einmalige VACUUM-Migration
            MaxBytes = 0,               // kein Eviction
        };

        using var sink = new SQLiteTelemetrySink(opts);
        await McpServer.RunAsync(sink, Console.OpenStandardInput(), Console.OpenStandardOutput());
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("heimdall mcp — Heimdall als MCP-Server (stdio, JSON-RPC 2.0)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Nutzung: heimdall mcp [--db <pfad>] [--help]");
        Console.Error.WriteLine("  --db <pfad>  Pfad zur bestehenden Heimdall-SQLite-DB");
        Console.Error.WriteLine("               (Default: $HEIMDALL_DB oder heimdall-otel.db)");
        Console.Error.WriteLine("  --help       Diese Hilfe (stderr; stdout bleibt fuer MCP sauber)");
    }
}
