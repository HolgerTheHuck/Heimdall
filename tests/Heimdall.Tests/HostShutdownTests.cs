#if NET10_0
using System.Net;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Heimdall;
using Heimdall.Host;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// C4 — Graceful Shutdown am Stand-alone-Host: ein OTLP-Trace wird gepostet (synchron
/// durable bei <c>Write*</c>-Rückkehr), dann der Host graceful gestoppt
/// (<c>IHost.StopAsync</c> → <c>ApplicationStopped</c>-Hook disposet den Sink NACH
/// Kestrel-Drain). Der Trace überlebt den Shutdown (frische DB-Verbindung), und der
/// Stopp wirft nicht — in-flight Writes committen vor dem Sink-Dispose.
/// </summary>
public class HostShutdownTests : HostBootTestBase
{
    [Fact]
    public async Task Graceful_Stop_Persistiert_Trace_Ohne_Throw()
    {
        // 1) Trace posten → synchron persistiert bei Write-Rückkehr.
        var req = BuildTraceRequest("shutdown-span");
        var content = new ByteArrayContent(req.ToByteArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        var resp = await Client.PostAsync("/otel/v1/traces", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, Query.CountSpans());

        // 2) Host-Optionen (DB-Pfad) + IHost vor dem Stopp auflösen.
        var opts = Services.GetRequiredService<HeimdallHostOptions>();
        var host = Services.GetRequiredService<IHost>();

        // 3) Graceful Stopp feuert ApplicationStopping → drain → ApplicationStopped
        //    (Sink-Dispose-Hook, C4). StopAsync blockiert bis zum Stopped-Zustand.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ex = await Record.ExceptionAsync(() => host.StopAsync(cts.Token));
        Assert.Null(ex);

        // 4) Trace überlebt den Shutdown — frische Verbindung (der Sink ist nun
        //    disposet, Query.CountSpans würde werfen). In-flight Writes sind safe.
        Assert.Equal(1L, RawCount(opts.Storage.DataPath, "heim_spans"));
    }

    private static long RawCount(string path, string table)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }
}
#endif