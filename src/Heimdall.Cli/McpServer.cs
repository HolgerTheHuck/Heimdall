using System.Text;
using System.Text.Json;

namespace Heimdall.Cli.Mcp;

// ---------------------------------------------------------------------------
// stdio-Transport: JSON-RPC 2.0 als newline-delimited JSON auf stdin/stdout.
// Antworten sind die einzige stdout-Ausgabe; Diagnosen gehoeren auf stderr
// (Program.cs), damit der Stream fuer den MCP-Client sauber bleibt.
// ---------------------------------------------------------------------------

internal static class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private const string ServerName = "heimdall";
    private const string ServerVersion = "1.4.0";

    public static async Task RunAsync(Heimdall.IHeimdallQuery query, Stream input, Stream output)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        await using var writer = new StreamWriter(output, new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(line);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue; // kaputte Zeile ignorieren (zeilenbasierter Stream)
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var methodEl))
                continue;

            var method = methodEl.GetString() ?? "";
            var hasId = root.TryGetProperty("id", out var idEl);
            var id = hasId ? idEl : default;

            string? resp = method switch
            {
                "initialize" when hasId => Result(id, InitializeResult()),
                "ping" when hasId => Result(id, new { }),
                "tools/list" when hasId => Result(id, new { tools = McpTools.All }),
                "tools/call" when hasId => HandleToolsCall(query, id, root),
                _ => hasId ? Error(id, -32601, $"Method not found: {method}") : null,
            };

            if (resp is not null) await writer.WriteLineAsync(resp);
        }
    }

    private static object InitializeResult() => new
    {
        protocolVersion = ProtocolVersion,
        capabilities = new { tools = new { } },
        serverInfo = new { name = ServerName, version = ServerVersion },
    };

    private static string HandleToolsCall(Heimdall.IHeimdallQuery query, JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object ||
            !p.TryGetProperty("name", out var nEl) || nEl.ValueKind != JsonValueKind.String ||
            !p.TryGetProperty("arguments", out var aEl))
            return Error(id, -32602, "Invalid params for tools/call");

        var name = nEl.GetString() ?? "";
        var args = aEl.ValueKind == JsonValueKind.Object ? aEl : default;

        try
        {
            var (text, isError) = McpTools.Call(query, name, args);
            return Result(id, new
            {
                content = new[] { new { type = "text", text } },
                isError,
            });
        }
        catch (Exception ex)
        {
            return Result(id, new
            {
                content = new[] { new { type = "text", text = "Tool error: " + ex.Message } },
                isError = true,
            });
        }
    }

    // --- JSON-RPC-Framing (id als JsonElement unveraendert durchreichen) ---

    private static string Result(JsonElement id, object result)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            WriteId(w, id);
            w.WritePropertyName("result");
            w.WriteRawValue(JsonSerializer.Serialize(result, McpTools.Opts), skipInputValidation: true);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Error(JsonElement id, int code, string message)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            WriteId(w, id);
            w.WritePropertyName("error");
            w.WriteStartObject();
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteId(Utf8JsonWriter w, JsonElement id)
    {
        if (id.ValueKind == JsonValueKind.Undefined) return;
        w.WritePropertyName("id");
        id.WriteTo(w);
    }
}
