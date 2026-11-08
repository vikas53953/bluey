// Boo.QaMockAi: a CI-only impostor for https://api.openai.com.
//
// Boo hard-pins https://api.openai.com:443 (src/Boo.Providers/ProviderEndpoint.cs), so the CI job
// points the hostname at 127.0.0.1 through the hosts file, trusts the self-signed certificate this
// process writes, and runs this server on :443. Every response is scripted from the request text.
//
// Nothing here touches production code. See README.md next to this file.

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Boo.QaMockAi;

/// <summary>Constants the UI test asserts against. Keep in sync with README.md.</summary>
public static class QaConstants
{
    /// <summary>Ring centre as a fraction of the captured frame (image pixels). The app maps frame -> display.</summary>
    public const double RingCenterXFraction = 0.25;
    public const double RingCenterYFraction = 0.40;
    /// <summary>Ring diameter as a fraction of the frame width.</summary>
    public const double RingSizeFraction = 0.08;
    public const string RingLabel = "QA ring";
    /// <summary>Fallback frame size when the request carries no framePixels= context.</summary>
    public const int FallbackFrameWidth = 1920;
    public const int FallbackFrameHeight = 1080;

    public const string ExplainText = "QA-EXPLAIN: This is the Boo QA Workspace. The ring marks the design preview button.";

    /// <summary>File edit: the relative path (under the user-selected file root) and marker written into it.</summary>
    public const string EditRelativePath = "MainWindow.xaml";
    public const string EditOriginalString = "Create account";
    public const string EditMarker = "QA-EDIT-APPLIED";
    public const string EditText = "QA-EDIT: I propose replacing the button text in MainWindow.xaml with " + EditMarker + ".";

    public const string ResearchText = "QA-RESEARCH: Three sources support this answer.";
    public static readonly (string Title, string Url)[] Sources =
    [
        ("QA source A", "https://example.com/a"),
        ("QA source B", "https://example.com/b"),
        ("QA source C", "https://example.com/c"),
    ];

    /// <summary>Decision labels (DecisionsClient.Decode accepts exactly these).</summary>
    public const string OffTask = "off_task";
    public const string OnTask = "on_task";
    public const string Uncertain = "uncertain";
    /// <summary>Classification requests numbered 1..N return off_task; after this many, on_task.</summary>
    public const int OffTaskRequestCount = 2;

    public const string NudgeText = "QA-NUDGE: Back to your task?";
    public const string NudgePointerTarget = "Scrollable geometry area";

    public const string DefaultText = "QA-DEFAULT: mock reply.";
}

public static class Program
{
    private static readonly object LogLock = new();
    private static string? _logPath;
    private static int _decisionCount;
    private static string? _workspaceXaml;

    public static async Task<int> Main(string[] args)
    {
        var port = 443;
        string? certOut = null;
        string? pfxOut = null;
        var pfxPassword = "boo-qa";
        string? workspace = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port": port = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cert-out": certOut = args[++i]; break;
                case "--pfx-out": pfxOut = args[++i]; break;
                case "--pfx-password": pfxPassword = args[++i]; break;
                case "--log": _logPath = args[++i]; break;
                case "--workspace": workspace = args[++i]; break;
                case "-h" or "--help":
                    Console.WriteLine("Boo.QaMockAi [--port 443] [--cert-out path.cer] [--pfx-out path.pfx] [--pfx-password pw] [--log path] [--workspace dir]");
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 2;
            }
        }

        _workspaceXaml = LoadWorkspaceXaml(workspace);

        var cert = CreateCertificate();
        if (certOut is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(certOut))!);
            File.WriteAllBytes(certOut, cert.Export(X509ContentType.Cert));
            Log($"[startup] wrote public cert (DER) to {certOut}");
        }
        var pfxBytes = cert.Export(X509ContentType.Pfx, pfxPassword);
        if (pfxOut is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pfxOut))!);
            File.WriteAllBytes(pfxOut, pfxBytes);
            Log($"[startup] wrote PFX to {pfxOut}");
        }
        // Re-import from PFX so the private key is usable by SChannel on Windows.
        // SChannel cannot use ephemeral keys, so on Windows the key goes to the (temporary) user key set.
        var serverCert = X509CertificateLoader.LoadPkcs12(pfxBytes, pfxPassword,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.Exportable : X509KeyStorageFlags.EphemeralKeySet);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
            k.ListenAnyIP(port, o =>
            {
                o.Protocols = HttpProtocols.Http1AndHttp2;
                o.UseHttps(serverCert);
            });
        });
        var app = builder.Build();

        app.Map("/{**path}", (RequestDelegate)HandleAsync);

        Log($"[startup] listening on https://0.0.0.0:{port} as api.openai.com (thumbprint {serverCert.Thumbprint})");
        Console.WriteLine($"Boo.QaMockAi listening on https://0.0.0.0:{port}");
        await app.RunAsync();
        return 0;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=api.openai.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("api.openai.com");
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
    }

    private static string? LoadWorkspaceXaml(string? workspace)
    {
        var candidates = new List<string>();
        if (workspace is not null) candidates.Add(Path.Combine(workspace, QaConstants.EditRelativePath));
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            candidates.Add(Path.Combine(dir, "tests", "fixtures", "native", "Boo.QaWorkspace", QaConstants.EditRelativePath));
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "tests", "fixtures", "native", "Boo.QaWorkspace", QaConstants.EditRelativePath));
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                Log($"[startup] edit template: {c}");
                return File.ReadAllText(c);
            }
        }
        Log("[startup] edit template not found; write_file will use a minimal XAML body");
        return null;
    }

    private static async Task HandleAsync(HttpContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync();
        var path = ctx.Request.Path.Value ?? "/";
        Log($"[request] {ctx.Request.Method} {path}{ctx.Request.QueryString}\n{Truncate(body)}");

        JsonNode? json = null;
        try { if (body.Length > 0) json = JsonNode.Parse(body); } catch (JsonException) { }

        var inputText = CollectInputText(json);
        var response = path switch
        {
            "/v1/decisions" => Decisions(inputText),
            "/v1/responses" => Responses(json, inputText),
            "/v1/models" => """{"object":"list","data":[{"id":"qa-mock","object":"model"}]}""",
            _ => """{"id":"qa-mock","object":"qa.mock","status":"completed","output":[]}"""
        };

        Log($"[response] {path}\n{Truncate(response)}");
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(response, Encoding.UTF8);
    }

    // ---- /v1/decisions (DecisionsClient) -------------------------------------------------------

    private static string Decisions(string inputText)
    {
        var count = Interlocked.Increment(ref _decisionCount);
        var text = inputText.ToLowerInvariant();
        string choice;
        if (text.Contains("resume") || text.Contains("qa-on-task") || count > QaConstants.OffTaskRequestCount)
            choice = QaConstants.OnTask;
        else if (text.Contains("qa-uncertain"))
            choice = QaConstants.Uncertain;
        else
            choice = QaConstants.OffTask;

        double on = 0.05, off = 0.05, unc = 0.05;
        switch (choice)
        {
            case QaConstants.OnTask: on = 0.90; break;
            case QaConstants.OffTask: off = 0.90; break;
            default: unc = 0.90; break;
        }
        var answer = new JsonObject
        {
            ["name"] = "activity",
            ["type"] = "choice",
            ["choice"] = choice,
            ["confidence"] = 0.9,
            ["probabilities"] = new JsonArray(
                new JsonObject { ["value"] = QaConstants.OnTask, ["probability"] = on },
                new JsonObject { ["value"] = QaConstants.OffTask, ["probability"] = off },
                new JsonObject { ["value"] = QaConstants.Uncertain, ["probability"] = unc })
        };
        return new JsonObject
        {
            ["id"] = $"qa-decision-{count}",
            ["object"] = "decision",
            ["answers"] = new JsonArray(answer)
        }.ToJsonString();
    }

    // ---- /v1/responses (ResponsesClient.RespondAsync / NudgeAsync) -----------------------------

    private static string Responses(JsonNode? json, string inputText)
    {
        var schemaName = json?["text"]?["format"]?["name"]?.GetValue<string>();
        var structured = schemaName is not null;
        var lower = inputText.ToLowerInvariant();

        if (schemaName == "boo_focus_nudge")
        {
            return Envelope(new JsonObject
            {
                ["text"] = QaConstants.NudgeText,
                ["pointer_target"] = QaConstants.NudgePointerTarget
            }.ToJsonString(), null);
        }

        var (frameW, frameH) = ParseFrame(inputText);

        if (lower.Contains("research") || lower.Contains("sources"))
        {
            var annotations = new JsonArray();
            foreach (var (title, url) in QaConstants.Sources)
            {
                annotations.Add(new JsonObject
                {
                    ["type"] = "url_citation",
                    ["title"] = title,
                    ["url"] = url,
                    ["start_index"] = 0,
                    ["end_index"] = QaConstants.ResearchText.Length
                });
            }
            var text = structured
                ? new JsonObject { ["text"] = QaConstants.ResearchText, ["marks"] = new JsonArray(), ["actions"] = new JsonArray() }.ToJsonString()
                : QaConstants.ResearchText;
            return Envelope(text, annotations, includeWebSearchCall: true);
        }

        if (lower.Contains("edit") || lower.Contains("rename") || lower.Contains("apply"))
        {
            var contents = (_workspaceXaml ?? $"<Window><Button Content=\"{QaConstants.EditOriginalString}\" /></Window>")
                .Replace(QaConstants.EditOriginalString, QaConstants.EditMarker, StringComparison.Ordinal);
            var action = new JsonObject
            {
                ["name"] = "write_file",
                ["target"] = QaConstants.EditRelativePath,
                ["arguments"] = new JsonObject
                {
                    ["x"] = null, ["y"] = null, ["automationId"] = null, ["name"] = null, ["text"] = null,
                    ["path"] = QaConstants.EditRelativePath,
                    ["overwrite"] = "true",
                    ["content"] = null,
                    ["contents"] = contents
                }
            };
            var result = new JsonObject
            {
                ["text"] = QaConstants.EditText,
                ["marks"] = new JsonArray(),
                ["actions"] = new JsonArray(action)
            };
            return Envelope(structured ? result.ToJsonString() : QaConstants.EditText, null);
        }

        if (lower.Contains("explain") || lower.Contains("what is on my screen") || lower.Contains("what's on my screen"))
        {
            var size = Math.Max(8, Math.Round(frameW * QaConstants.RingSizeFraction));
            var x = Math.Round(frameW * QaConstants.RingCenterXFraction - size / 2);
            var y = Math.Round(frameH * QaConstants.RingCenterYFraction - size / 2);
            var mark = new JsonObject
            {
                ["kind"] = "ring",
                ["x"] = x, ["y"] = y, ["width"] = size, ["height"] = size,
                ["label"] = QaConstants.RingLabel,
                ["endX"] = 0, ["endY"] = 0
            };
            var result = new JsonObject
            {
                ["text"] = QaConstants.ExplainText,
                ["marks"] = new JsonArray(mark),
                ["actions"] = new JsonArray()
            };
            return Envelope(structured ? result.ToJsonString() : QaConstants.ExplainText, null);
        }

        var fallback = new JsonObject { ["text"] = QaConstants.DefaultText, ["marks"] = new JsonArray(), ["actions"] = new JsonArray() };
        return Envelope(structured ? fallback.ToJsonString() : QaConstants.DefaultText, null);
    }

    /// <summary>Wraps text in a Responses API envelope: output[] -> message -> content[] -> output_text.</summary>
    private static string Envelope(string outputText, JsonArray? annotations, bool includeWebSearchCall = false)
    {
        var part = new JsonObject
        {
            ["type"] = "output_text",
            ["text"] = outputText,
            ["annotations"] = annotations ?? new JsonArray()
        };
        var output = new JsonArray();
        if (includeWebSearchCall)
        {
            var sources = new JsonArray();
            foreach (var (title, url) in QaConstants.Sources)
                sources.Add(new JsonObject { ["type"] = "url", ["title"] = title, ["url"] = url });
            output.Add(new JsonObject
            {
                ["type"] = "web_search_call",
                ["id"] = "ws_qa",
                ["status"] = "completed",
                ["action"] = new JsonObject { ["type"] = "search", ["query"] = "qa", ["sources"] = sources }
            });
        }
        output.Add(new JsonObject
        {
            ["type"] = "message",
            ["id"] = "msg_qa",
            ["status"] = "completed",
            ["role"] = "assistant",
            ["content"] = new JsonArray(part)
        });
        return new JsonObject
        {
            ["id"] = "resp_qa",
            ["object"] = "response",
            ["status"] = "completed",
            ["model"] = "qa-mock",
            ["output"] = output,
            ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0, ["total_tokens"] = 0 }
        }.ToJsonString();
    }

    private static readonly Regex FramePixels = new(@"framePixels=(\d+)x(\d+)", RegexOptions.Compiled);

    private static (int W, int H) ParseFrame(string text)
    {
        var m = FramePixels.Match(text);
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var w) &&
            int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var h) &&
            w > 0 && h > 0)
            return (w, h);
        return (QaConstants.FallbackFrameWidth, QaConstants.FallbackFrameHeight);
    }

    /// <summary>All input_text parts (plus instructions) joined; input_image data is skipped.</summary>
    private static string CollectInputText(JsonNode? json)
    {
        if (json is null) return string.Empty;
        var sb = new StringBuilder();
        if (json["instructions"] is JsonValue instr) sb.Append(instr.ToString()).Append('\n');
        if (json["input"] is JsonArray input)
        {
            foreach (var item in input)
            {
                if (item?["content"] is JsonArray content)
                {
                    foreach (var part in content)
                    {
                        if (part?["type"]?.ToString() == "input_text" && part["text"] is JsonValue t)
                            sb.Append(t.ToString()).Append('\n');
                    }
                }
                else if (item?["content"] is JsonValue plain)
                {
                    sb.Append(plain.ToString()).Append('\n');
                }
            }
        }
        return sb.ToString();
    }

    private static string Truncate(string text)
    {
        // Strip base64 image payloads so the log stays readable.
        var cleaned = Regex.Replace(text, @"data:image/[a-z]+;base64,[A-Za-z0-9+/=]+", "data:image/...;base64,<omitted>");
        return cleaned.Length > 20000 ? cleaned[..20000] + "...<truncated>" : cleaned;
    }

    private static void Log(string line)
    {
        var stamped = $"{DateTimeOffset.UtcNow:O} {line}";
        Console.WriteLine(stamped);
        if (_logPath is null) return;
        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_logPath))!);
                File.AppendAllText(_logPath, stamped + Environment.NewLine);
            }
            catch (IOException) { }
        }
    }
}
