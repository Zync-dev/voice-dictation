using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace Murmur;

/// <summary>
/// AI providers: developer checks. Murmur.exe --providertest out.txt
/// (a) builds requests for every provider against a mock server on localhost and checks address, headers and body
/// (token limit name, reasoning_effort, JSON mode, images, the Anthropic SDK's request, refusals, errors, model lists,
/// speech forms); (b) migrates old settings files; (c) makes a few real calls to Groq with the test settings' key
/// (only when MURMUR_DATA_DIR is set; MURMUR_PROVIDERTEST_REAL=0 skips them). Exit code 1 on a failure.
/// </summary>
public static class ProviderTests
{
    public static bool TryRun(Application app, string[] args)
    {
        if (args.Length == 2 && args[0] == "--providertest") { _ = Finish(app, args[1]); return true; }
        return false;
    }

    private static int _pass, _fail;

    private static async Task Finish(Application app, string outPath)
    {
        var lines = new List<string>();
        _pass = _fail = 0;
        try
        {
            await MockChecksAsync(lines);
            MigrationChecks(lines);
            await RealGroqAsync(lines);
        }
        catch (Exception ex) { lines.Add("FAILED " + ex); _fail++; }
        lines.Insert(0, $"{_pass} passed, {_fail} failed");
        File.WriteAllLines(outPath, lines);
        app.Shutdown(_fail == 0 ? 0 : 1);
    }

    private static void Check(List<string> lines, bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        lines.Add($"{(ok ? "ok  " : "FAIL")} {what}");
    }

    // ================================================================ (a) mock server

    private sealed record Recorded(string Method, string Path, Dictionary<string, string> Headers, string Body)
    {
        public JsonNode? Json { get { try { return JsonNode.Parse(Body); } catch { return null; } } }
        public string Header(string name) => Headers.GetValueOrDefault(name.ToLowerInvariant()) ?? "";
    }

    /// <summary>A tiny HTTP server on localhost that records each request and answers like the providers do.</summary>
    private sealed class MockServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public readonly List<Recorded> Requests = [];
        public string Root { get; }

        public MockServer()
        {
            int port = 0;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                port = Random.Shared.Next(41000, 49000);
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://localhost:{port}/");
                    _listener.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 19) { }
            }
            Root = $"http://localhost:{port}";
            _ = Task.Run(LoopAsync);
        }

        public Recorded Last => Requests[^1];

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                try { Answer(ctx); } catch (Exception ex) { Log.Write("Mock server: " + ex.Message); }
            }
        }

        private void Answer(HttpListenerContext ctx)
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
            var headers = new Dictionary<string, string>();
            foreach (string? k in ctx.Request.Headers.AllKeys) if (k != null) headers[k.ToLowerInvariant()] = ctx.Request.Headers[k] ?? "";
            string path = ctx.Request.Url!.AbsolutePath;
            var rec = new Recorded(ctx.Request.HttpMethod, path, headers, body);
            lock (Requests) Requests.Add(rec);
            var (status, json) = Route(rec);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }

        private static (int, string) Route(Recorded r)
        {
            string model = r.Json?["model"]?.GetValue<string>() ?? "";
            if (r.Path.StartsWith("/wrongpath")) return (404, "404 page not found"); // Ollama without /v1
            if (r.Path.EndsWith("/v1/messages"))
            {
                if (model == "error-401") return (401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
                if (model == "error-400") return (400, """{"type":"error","error":{"type":"invalid_request_error","message":"temperature is not supported on this model"},"request_id":"req_1"}""");
                if (model == "refuse") return (200, AnthropicMessage(model, "[]", "refusal"));
                return (200, AnthropicMessage(model, """[{"type":"thinking","thinking":"","signature":"x"},{"type":"text","text":"OK"}]""", "end_turn"));
            }
            if (r.Path.EndsWith("/v1/models") && r.Path.StartsWith("/anthropic"))
                return (200, """{"data":[{"id":"claude-opus-5-5","type":"model","display_name":"Claude Opus 5.5","created_at":"2026-01-01T00:00:00Z"},{"id":"claude-haiku-5-5","type":"model","display_name":"Claude Haiku 5.5","created_at":"2026-01-01T00:00:00Z"}],"has_more":false,"first_id":"claude-opus-5-5","last_id":"claude-haiku-5-5"}""");
            if (r.Path.EndsWith("/chat/completions"))
            {
                if (model == "nojson" && r.Json?["response_format"] != null) return (400, """{"error":{"message":"response_format is not supported"}}""");
                if (model == "gemini-noeffort" && r.Json?["reasoning_effort"] != null) return (400, """{"error":{"message":"reasoning_effort 'minimal' is not supported by this model"}}""");
                if (model == "gemini-badkey") return (400, """[{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT","details":[{"reason":"API_KEY_INVALID"}]}}]""");
                if (model == "html") return (200, "<html><body>Sign in to the Wi-Fi</body></html>");
                if (model.StartsWith("error-")) return (int.Parse(model[6..]), """{"error":{"message":"Invalid API Key"}}""");
                if (model == "mistral-parts")
                    return (200, """{"choices":[{"message":{"role":"assistant","content":[{"type":"thinking","thinking":[{"type":"text","text":"hmm"}]},{"type":"text","text":"OK"}]}}]}""");
                return (200, """{"choices":[{"message":{"role":"assistant","content":" OK "}}]}""");
            }
            if (r.Path.EndsWith("/models"))
            {
                if (r.Path.StartsWith("/groq"))
                    return (200, """{"object":"list","data":[{"id":"openai/gpt-oss-120b","active":true},{"id":"llama-3.3-70b-versatile","active":true},{"id":"whisper-large-v3","active":true},{"id":"old-model","active":false},{"id":"meta-llama/llama-prompt-guard-2-86m","active":true},{"id":"new/brand-model","active":true}]}""");
                if (r.Path.StartsWith("/mistral"))
                    return (200, """[{"id":"mistral-small-latest","capabilities":{"completion_chat":true},"archived":false},{"id":"mistral-embed","capabilities":{"completion_chat":false}},{"id":"mistral-old","capabilities":{"completion_chat":true},"archived":true},{"id":"voxtral-mini-latest","capabilities":{"completion_chat":true}},{"id":"mistral-large-latest","capabilities":{"completion_chat":true}}]""");
                if (r.Path.StartsWith("/gemini"))
                    return (200, """{"object":"list","data":[{"id":"models/gemini-3.1-flash-lite"},{"id":"models/gemini-4-carbon"},{"id":"models/gemini-embedding-002"},{"id":"models/imagen-5"}]}""");
                if (r.Path.StartsWith("/openai"))
                    return (200, """{"object":"list","data":[{"id":"gpt-6-luna"},{"id":"gpt-6.2-sol"},{"id":"gpt-transcribe"},{"id":"gpt-6-image"},{"id":"text-embedding-4"},{"id":"gpt-6-luna-realtime"}]}""");
                return (200, """{"data":[{"id":"llama3.3"},{"id":"nomic-embed-text"}]}""");
            }
            if (r.Path.EndsWith("/audio/transcriptions"))
                return (200, r.Path.StartsWith("/openai") ? """{"text":"Hej verden.","languages":["da"]}""" : """{"text":"Hej verden.","language":"danish"}""");
            return (404, """{"error":{"message":"not found"}}""");
        }

        private static string AnthropicMessage(string model, string content, string stop) =>
            $$$"""{"id":"msg_01","type":"message","role":"assistant","model":"{{{model}}}","content":{{{content}}},"stop_reason":"{{{stop}}}","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":2}}""";

        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
    }

    private static AiTarget T(MockServer m, AiProvider p, string model, string key = "test-key", string? path = null) =>
        new(p, m.Root + (path ?? "/" + p.Id + "/v1"), model, key);

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");

    private static async Task MockChecksAsync(List<string> lines)
    {
        lines.Add("== requests against a mock server ==");
        using var m = new MockServer();
        lines.Add("mock server at " + m.Root);
        string Has(JsonNode? n, string field) => n?[field]?.ToJsonString() ?? "(none)";
        List<Recorded> lastTwo;

        // ---- Groq: as before providers ----
        string answer = await AiService.ChatAsync(T(m, AiProviders.Groq, "openai/gpt-oss-120b"), new AiRequest("sys", "hej"));
        var r = m.Last;
        Check(lines, answer == "OK" && r.Path == "/groq/v1/chat/completions" && r.Header("Authorization") == "Bearer test-key",
            $"Groq: POST {r.Path}, bearer key, answer trimmed [{answer}]");
        Check(lines, r.Json?["reasoning_effort"]?.GetValue<string>() == "low" && r.Json?["temperature"] == null && r.Json?["n"] == null && r.Json?["max_completion_tokens"] == null,
            $"Groq gpt-oss plain call (polish): reasoning_effort low (the recommended model must not think at medium), no temperature, n or token limit ({r.Body.Length} bytes)");
        Check(lines, OpenAiCompatibleAdapter.ReasoningEffort(AiProviders.CustomId, "gpt-oss-120b", false) == null && OpenAiCompatibleAdapter.ReasoningEffort(AiProviders.CustomId, "gpt-oss-120b", true) == "low",
            "Custom gpt-oss: as before providers (low only for the short hands-free calls)");
        Check(lines, r.Json?["messages"]?[0]?["role"]?.GetValue<string>() == "system" && r.Json?["messages"]?[1]?["content"]?.GetValue<string>() == "hej",
            "Groq: system and user messages");
        await AiService.ChatAsync(T(m, AiProviders.Groq, "openai/gpt-oss-120b"), new AiRequest("sys", "hej") { Fast = true, Json = true, MaxTokens = 50 });
        r = m.Last;
        Check(lines, r.Json?["reasoning_effort"]?.GetValue<string>() == "low" && r.Json?["response_format"]?["type"]?.GetValue<string>() == "json_object"
                     && r.Json?["max_completion_tokens"]?.GetValue<int>() == 50 && r.Json?["max_tokens"] == null,
            $"Groq gpt-oss fast JSON call: reasoning_effort {Has(r.Json, "reasoning_effort")}, json_object, max_completion_tokens 50");
        await AiService.ChatAsync(T(m, AiProviders.Groq, "llama-3.3-70b-versatile"), new AiRequest("sys", "hej") { Fast = true });
        Check(lines, m.Last.Json?["reasoning_effort"] == null, "Groq llama: no reasoning_effort (it takes none)");
        await AiService.ChatAsync(T(m, AiProviders.Groq, "qwen/qwen3.8-27b"), new AiRequest("sys", "what is this") { Image = Png, ImageType = "image/png", Fast = true });
        r = m.Last;
        Check(lines, r.Json?["messages"]?[1]?["content"]?[1]?["image_url"]?["url"]?.GetValue<string>()?.StartsWith("data:image/png;base64,") == true
                     && r.Json?["messages"]?[1]?["content"]?[0]?["type"]?.GetValue<string>() == "text",
            "Groq image: text part and image_url {url: data:image/png;base64,...}");

        // ---- OpenAI ----
        await AiService.ChatAsync(T(m, AiProviders.OpenAi, "gpt-6-luna"), new AiRequest("sys", "hej") { MaxTokens = 200 });
        r = m.Last;
        Check(lines, r.Path == "/openai/v1/chat/completions" && r.Json?["reasoning_effort"]?.GetValue<string>() == "none" && r.Json?["max_completion_tokens"]?.GetValue<int>() == 200
                     && r.Json?["max_tokens"] == null && r.Json?["temperature"] == null && r.Json?["n"] == null,
            $"OpenAI gpt-6-luna: reasoning_effort none, max_completion_tokens, no max_tokens/temperature/n");
        await AiService.ChatAsync(T(m, AiProviders.OpenAi, "gpt-6.2-sol"), new AiRequest("sys", "hej"));
        Check(lines, m.Last.Json?["reasoning_effort"]?.GetValue<string>() == "low", "OpenAI gpt-6.2-sol: reasoning_effort low (it refuses none)");
        await AiService.ChatAsync(T(m, AiProviders.OpenAi, "gpt-6.2-astra"), new AiRequest("sys", "hej"));
        Check(lines, m.Last.Json?["reasoning_effort"]?.GetValue<string>() == "low", "OpenAI gpt-6.2-astra: reasoning_effort low");
        await AiService.ChatAsync(T(m, AiProviders.OpenAi, "gpt-6-luna"), new AiRequest("sys", "read") { Image = Png, ImageType = "image/png", Json = true });
        r = m.Last;
        Check(lines, r.Json?["messages"]?[1]?["content"]?[1]?["image_url"]?["url"] != null && r.Json?["response_format"]?["type"]?.GetValue<string>() == "json_object",
            "OpenAI image and JSON mode");

        // ---- Gemini ----
        await AiService.ChatAsync(T(m, AiProviders.Gemini, "gemini-3.1-flash-lite", path: "/gemini/v1beta/openai/"), new AiRequest("sys", "hej") { MaxTokens = 100 });
        r = m.Last;
        Check(lines, r.Path == "/gemini/v1beta/openai/chat/completions" && r.Json?["reasoning_effort"]?.GetValue<string>() == "minimal" && r.Json?["max_tokens"]?.GetValue<int>() == 100
                     && r.Json?["max_completion_tokens"] == null,
            $"Gemini: base with trailing slash -> {r.Path}, reasoning_effort minimal, max_tokens");

        answer = await AiService.ChatAsync(T(m, AiProviders.Gemini, "gemini-noeffort", path: "/gemini/v1beta/openai/"), new AiRequest("sys", "{}") { Json = true });
        lastTwo = m.Requests.TakeLast(2).ToList();
        Check(lines, answer == "OK" && lastTwo[0].Json?["reasoning_effort"] != null && lastTwo[1].Json?["reasoning_effort"] == null && lastTwo[1].Json?["response_format"] != null,
            "A model that refuses reasoning_effort: 400, then the same request without it (JSON mode kept)");

        // ---- Mistral ----
        await AiService.ChatAsync(T(m, AiProviders.Mistral, "mistral-small-latest"), new AiRequest("sys", "hej") { MaxTokens = 100, Image = Png, ImageType = "image/png" });
        r = m.Last;
        Check(lines, r.Json?["reasoning_effort"] == null && r.Json?["max_tokens"]?.GetValue<int>() == 100 && r.Json?["max_completion_tokens"] == null
                     && r.Json?["messages"]?[1]?["content"]?[1]?["image_url"]?.GetValue<string>()?.StartsWith("data:image/png;base64,") == true,
            "Mistral Small: no reasoning_effort, max_tokens, image_url as a plain string");
        await AiService.ChatAsync(T(m, AiProviders.Mistral, "mistral-large-latest"), new AiRequest("sys", "hej"));
        Check(lines, m.Last.Json?["reasoning_effort"]?.GetValue<string>() == "low", "Mistral Large: reasoning_effort low (it refuses none)");
        answer = await AiService.ChatAsync(T(m, AiProviders.Mistral, "mistral-parts"), new AiRequest("sys", "hej"));
        Check(lines, answer == "OK", $"Mistral content as parts (thinking + text): only the text [{answer}]");

        // ---- Custom (Ollama on this PC: no key) ----
        await AiService.ChatAsync(T(m, AiProviders.Custom, "llama3.3", key: ""), new AiRequest("sys", "hej") { Fast = true, MaxTokens = 10 });
        r = m.Last;
        Check(lines, r.Header("Authorization") == "" && r.Json?["reasoning_effort"] == null && r.Json?["max_tokens"]?.GetValue<int>() == 10,
            "Custom local: no Authorization header, no reasoning_effort, max_tokens");
        answer = await AiService.ChatAsync(T(m, AiProviders.Custom, "nojson", key: ""), new AiRequest("sys", "{}") { Json = true });
        lastTwo = m.Requests.TakeLast(2).ToList();
        Check(lines, answer == "OK" && lastTwo[0].Json?["response_format"] != null && lastTwo[1].Json?["response_format"] == null,
            "A service without JSON mode: 400, then the same request without response_format");

        // ---- errors in plain words ----
        foreach (var (code, expect) in new[] { (401, "did not accept the key"), (404, "does not offer the model"), (429, "too many requests"), (500, "has a problem") })
        {
            var target = T(m, AiProviders.Groq, "error-" + code);
            string? problem = await AiService.TestAsync(target);
            Check(lines, problem?.Contains(expect) == true, $"Test button, HTTP {code}: \"{problem}\"");
        }
        string? ok = await AiService.TestAsync(T(m, AiProviders.OpenAi, "gpt-6-luna"));
        r = m.Last;
        Check(lines, ok == null && r.Json?["max_completion_tokens"]?.GetValue<int>() == 200, "Test button: one tiny request (max 200 tokens), OK");

        // ---- Anthropic through the official SDK ----
        var anthropic = new AiTarget(AiProviders.Anthropic, m.Root + "/anthropic", "claude-opus-5-5", "sk-ant-test");
        answer = await AiService.ChatAsync(anthropic, new AiRequest("You polish text.", "hej verden"));
        r = m.Last;
        Check(lines, answer == "OK" && r.Path == "/anthropic/v1/messages", $"Anthropic: POST {r.Path}, text from the text block only [{answer}]");
        // SDK 13.x sends the API key as "Authorization: Bearer" (older SDKs used x-api-key); either way it is the key from the settings, never an environment variable.
        Check(lines, (r.Header("x-api-key") == "sk-ant-test" || r.Header("Authorization") == "Bearer sk-ant-test") && r.Header("anthropic-version").Length > 0,
            $"Anthropic: the settings' key ({(r.Header("x-api-key").Length > 0 ? "x-api-key" : "bearer")}), anthropic-version {r.Header("anthropic-version")}, to the address Murmur set");
        Check(lines, r.Json?["model"]?.GetValue<string>() == "claude-opus-5-5" && r.Json?["system"]?.GetValue<string>() == "You polish text."
                     && r.Json?["max_tokens"]?.GetValue<int>() == AnthropicAdapter.DefaultMaxTokens
                     && r.Json?["output_config"]?["effort"]?.GetValue<string>() == "low"
                     && r.Json?["temperature"] == null && r.Json?["thinking"] == null && r.Json?["top_p"] == null,
            $"Anthropic body: model, system field, max_tokens {Has(r.Json, "max_tokens")}, output_config {Has(r.Json, "output_config")}, no temperature or thinking");
        Check(lines, r.Json?["messages"]?.AsArray().Count == 1 && r.Json?["messages"]?[0]?["role"]?.GetValue<string>() == "user",
            "Anthropic: one user message, no assistant prefill");
        await AiService.ChatAsync(anthropic, new AiRequest("Route this.", "svar på mailen") { Json = true, Image = Png, ImageType = "image/png" });
        r = m.Last;
        var block = r.Json?["messages"]?[0]?["content"]?[0];
        Check(lines, block?["type"]?.GetValue<string>() == "image" && block?["source"]?["type"]?.GetValue<string>() == "base64"
                     && block?["source"]?["media_type"]?.GetValue<string>() == "image/png" && (block?["source"]?["data"]?.GetValue<string>()?.Length ?? 0) > 20
                     && r.Json?["messages"]?[0]?["content"]?[1]?["type"]?.GetValue<string>() == "text",
            "Anthropic image: a base64 image block, then the text");
        Check(lines, r.Json?["system"]?.GetValue<string>()?.Contains("JSON object only") == true && r.Json?["response_format"] == null,
            "Anthropic JSON: asked for in the system prompt");
        await AiService.ChatAsync(anthropic with { Model = "claude-3-7-sonnet" }, new AiRequest("s", "u"));
        Check(lines, m.Last.Json?["output_config"] == null, "Anthropic older model: no effort");
        try
        {
            await AiService.ChatAsync(anthropic with { Model = "refuse" }, new AiRequest("s", "u"));
            Check(lines, false, "Anthropic refusal throws");
        }
        catch (InvalidOperationException ex) { Check(lines, ex.Message.Contains("declined"), $"Anthropic refusal: \"{ex.Message}\""); }
        string? anthropicProblem = await AiService.TestAsync(anthropic with { Model = "error-401" });
        Check(lines, anthropicProblem?.Contains("did not accept the key") == true, $"Anthropic 401 in plain words: \"{anthropicProblem}\"");
        anthropicProblem = await AiService.TestAsync(anthropic with { Model = "error-400" });
        Check(lines, anthropicProblem == "Anthropic turned the request down: temperature is not supported on this model",
            $"Anthropic 400: the API's own message, not the SDK's status text and JSON: \"{anthropicProblem}\"");
        // A closed port on this PC: the SDK throws AnthropicIOException, which must come out as "Could not reach".
        var offline = anthropic with { BaseUrl = "http://127.0.0.1:9" };
        anthropicProblem = await AiService.TestAsync(offline);
        Check(lines, anthropicProblem?.StartsWith("Could not reach Anthropic") == true, $"Anthropic offline, Test button: \"{anthropicProblem}\"");
        try { await AiService.RefreshModelsAsync(new AppSettings(), offline); Check(lines, false, "Anthropic offline model list throws"); }
        catch (InvalidOperationException ex) { Check(lines, ex.Message.StartsWith("Could not reach Anthropic"), $"Anthropic offline, model list: \"{ex.Message}\""); }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await AiService.TestAsync(anthropic, cancelled.Token); Check(lines, false, "A cancelled test throws"); }
            catch (OperationCanceledException) { Check(lines, true, "A cancelled test (another card clicked) ends with OperationCanceledException, which the panel catches"); }
        }

        // ---- Gemini's errors come as an array; a bad Gemini key is a 400 ----
        int before = m.Requests.Count;
        string? geminiProblem = await AiService.TestAsync(T(m, AiProviders.Gemini, "gemini-badkey"));
        Check(lines, geminiProblem?.Contains("did not accept the key") == true, $"Gemini bad key (400 in an array): \"{geminiProblem}\"");
        before = m.Requests.Count;
        try { await AiService.ChatAsync(T(m, AiProviders.Gemini, "gemini-badkey"), new AiRequest("s", "u") { Json = true }); }
        catch (InvalidOperationException ex) { Check(lines, m.Requests.Count - before == 1 && ex.Message.Contains("API key not valid"), $"Gemini bad key, JSON call: one request, no retry without JSON mode ({m.Requests.Count - before}): {ex.Message}"); }

        // ---- Custom: wrong path, an answer that is not JSON, names in messages, plain http ----
        string? customProblem = await AiService.TestAsync(new AiTarget(AiProviders.Custom, m.Root + "/wrongpath", "llama3.2", ""));
        Check(lines, customProblem?.StartsWith("Nothing answered at " + m.Root + "/wrongpath/chat/completions") == true, $"Custom 404 without the model named: the address, not the model: \"{customProblem}\"");
        customProblem = await AiService.TestAsync(new AiTarget(AiProviders.Custom, m.Root + "/custom/v1", "html", ""));
        Check(lines, customProblem?.Contains("not from an AI API") == true && !customProblem.Contains("Custom"), $"Custom answer that is not JSON (a Wi-Fi sign-in page): \"{customProblem}\"");
        customProblem = await AiService.TestAsync(new AiTarget(AiProviders.Custom, m.Root + "/custom/v1", "error-401", "k"));
        Check(lines, customProblem?.StartsWith("The service at localhost:") == true, $"Custom in messages: the host, not \"Custom\": \"{customProblem}\"");
        Check(lines, AiConfig.InsecureAddressWarning("http://my-server:8000/v1") != null && AiConfig.InsecureAddressWarning("http://localhost:11434/v1") == null
                     && AiConfig.InsecureAddressWarning("http://127.0.0.1:1234/v1") == null && AiConfig.InsecureAddressWarning("https://openrouter.ai/api/v1") == null,
            "Plain http:// to another machine is warned about; https and this PC are not");
        var leftover = new AppSettings { AiProvider = AiProviders.CustomId, LlmBaseUrl = "https://api.groq.com/openai/v1" };
        Check(lines, AiConfig.BaseUrl(leftover, AiProviders.Custom) == "" && AiConfig.Target(leftover) == null,
            "Custom never uses the old Groq default address (a Custom key is not sent to Groq)");

        // ---- the vision model follows the provider ----
        var vs = new AppSettings { AiProvider = AiProviders.GroqId, ScreenVisionModel = "qwen/qwen3.8-27b" };
        AiConfig.SetKey(vs, AiProviders.GroqId, "gsk_x");
        AiConfig.SetKey(vs, AiProviders.AnthropicId, "sk-ant-x");
        string groqVision = AiConfig.VisionModel(vs);
        vs.AiProvider = AiProviders.AnthropicId;
        string anthropicVision = AiConfig.VisionModel(vs);
        vs.AiProvider = AiProviders.GroqId;
        vs.ScreenVisionModel = "";
        string groqEmpty = AiConfig.VisionModel(vs);
        vs.AiProvider = AiProviders.OpenAiId;
        string openAiEmpty = AiConfig.VisionModel(vs);
        Check(lines, groqVision == "qwen/qwen3.8-27b" && anthropicVision == "claude-opus-5-5" && groqEmpty == "" && openAiEmpty == "gpt-6-luna",
            $"Vision model per provider: Groq {groqVision}; after a switch to Anthropic {anthropicVision} (not the Groq model); empty on Groq: none; empty on OpenAI: the chat model {openAiEmpty}");

        // ---- model lists ----
        var s = new AppSettings { AiProvider = AiProviders.GroqId };
        var ids = await AiService.RefreshModelsAsync(s, T(m, AiProviders.Groq, "x"));
        Check(lines, ids.SequenceEqual(["openai/gpt-oss-120b", "llama-3.3-70b-versatile", "new/brand-model"]), "Groq list: inactive, whisper and prompt-guard dropped: " + string.Join(", ", ids));
        var list = AiService.ModelsFor(s, AiProviders.Groq);
        Check(lines, list[0].Id == "openai/gpt-oss-120b" && list[0].Recommended && list.Any(x => x.Id == "new/brand-model" && x.Name == "new/brand-model")
                     && !list.Any(x => x.Id == "llama-3.1-8b-instant") && s.AiModelCache[AiProviders.GroqId].Ids.Count == 3,
            "Groq drop-down: recommended first, a new model added, a described model the key lacks left out, cached: " + string.Join(", ", list.Select(x => x.Id)));
        s.AiModels[AiProviders.GroqId] = "llama-3.1-8b-instant";
        Check(lines, AiService.ModelsFor(s, AiProviders.Groq).Any(x => x.Id == "llama-3.1-8b-instant"), "The chosen model stays in the drop-down even when the list lacks it");
        ids = await AiService.RefreshModelsAsync(s, T(m, AiProviders.Mistral, "x"));
        Check(lines, ids.SequenceEqual(["mistral-small-latest", "mistral-large-latest"]), "Mistral list (a bare array): non-chat, archived and voxtral dropped: " + string.Join(", ", ids));
        ids = await AiService.RefreshModelsAsync(s, T(m, AiProviders.Gemini, "x", path: "/gemini/v1beta/openai/"));
        Check(lines, ids.SequenceEqual(["gemini-3.1-flash-lite", "gemini-4-carbon"]), "Gemini list: models/ prefix stripped, embedding and imagen dropped: " + string.Join(", ", ids));
        ids = await AiService.RefreshModelsAsync(s, T(m, AiProviders.OpenAi, "x"));
        Check(lines, ids.SequenceEqual(["gpt-6-luna", "gpt-6.2-sol"]), "OpenAI list: transcribe, image, embedding and realtime dropped: " + string.Join(", ", ids));
        ids = await AiService.RefreshModelsAsync(s, anthropic);
        Check(lines, ids.SequenceEqual(["claude-opus-5-5", "claude-haiku-5-5"]) && m.Last.Path == "/anthropic/v1/models", "Anthropic list through the SDK: " + string.Join(", ", ids));
        Check(lines, AiService.ModelsFor(new AppSettings(), AiProviders.OpenAi).Select(x => x.Id).SequenceEqual(AiProviders.OpenAi.Models.Select(x => x.Id)),
            "Offline (nothing fetched): Murmur's own list");

        // ---- key hints ----
        foreach (var (p, key, expect) in new (AiProvider, string, string?)[]
                 {
                     (AiProviders.Groq, "gsk_abc", null), (AiProviders.Groq, "sk-proj-abc", "OpenAI key"), (AiProviders.OpenAi, "sk-ant-api03-x", "Anthropic key"),
                     (AiProviders.Anthropic, "sk-ant-api03-x", null), (AiProviders.Anthropic, "abc", "start with sk-ant-"), (AiProviders.Mistral, "gsk_x", "Groq key"),
                     (AiProviders.Mistral, "Ab12Cd34", null), (AiProviders.Gemini, "AQ.abc", null), (AiProviders.Custom, "sk-abc", null), (AiProviders.OpenAi, "sk-a bc", "space"),
                 })
        {
            string? w = AiProviders.KeyWarning(p, key);
            Check(lines, expect == null ? w == null : w?.Contains(expect) == true, $"Key hint {p.Name} \"{key}\": {w ?? "none"}");
        }
        Check(lines, AiProviders.All.All(p => p.IsCustom || (p.Recommended != null && p.Models.Count(x => x.Recommended) == 1 && p.KeyPage.StartsWith("https://") && p.Steps.Length >= 3)),
            "Every provider has one recommended model, a key page and at least three steps");

        // ---- speech ----
        await SpeechFormAsync(lines, m);
    }

    private static async Task SpeechFormAsync(List<string> lines, MockServer m)
    {
        async Task<string> Form(SttTarget t, string format, string? language, bool timestamps = false)
        {
            using var form = new MultipartFormDataContent();
            CloudStt.AddFields(form, t, format, language, "Zync, Murmur", timestamps);
            return await form.ReadAsStringAsync();
        }
        static bool Field(string form, string name, string? value = null) =>
            form.Contains($"name={name}") || form.Contains($"name=\"{name}\"") ? value == null || form.Contains(value) : false;

        var groq = new SttTarget(AiProviders.Groq, "x", "whisper-large-v3-turbo", "k");
        string f = await Form(groq, CloudStt.DetectFormat(groq), "da");
        Check(lines, Field(f, "response_format", "verbose_json") && Field(f, "language", "da") && Field(f, "prompt") && !Field(f, "languages[]"),
            "Speech Groq: verbose_json, language, prompt");
        var openai = new SttTarget(AiProviders.OpenAi, "x", "gpt-transcribe", "k");
        f = await Form(openai, CloudStt.DetectFormat(openai), "da");
        Check(lines, Field(f, "response_format", "json") && !f.Contains("verbose_json") && Field(f, "languages[]", "da") && !Field(f, "language"),
            "Speech OpenAI gpt-transcribe: json, languages[] instead of language");
        f = await Form(openai with { Model = "whisper-1" }, "verbose_json", "da");
        Check(lines, Field(f, "language", "da") && f.Contains("verbose_json"), "Speech OpenAI whisper-1: language and verbose_json as before");
        var mistral = new SttTarget(AiProviders.Mistral, "x", "voxtral-mini-transcribe-latest", "k");
        f = await Form(mistral, "verbose_json", null, timestamps: true);
        Check(lines, !Field(f, "response_format") && Field(f, "timestamp_granularities[]", "segment") && !Field(f, "prompt"),
            "Speech Mistral: no response_format or prompt, segment timestamps");
        Check(lines, CloudStt.ReportedLanguage(JsonNode.Parse("""{"text":"x","languages":["da","en"]}""")!) == "da"
                     && CloudStt.ReportedLanguage(JsonNode.Parse("""{"text":"x","language":"danish"}""")!) == "danish"
                     && CloudStt.ReportedLanguage(JsonNode.Parse("""{"text":"x","language":null}""")!) == null,
            "Speech: language read from language or languages");

        // Which address, model and key a settings object resolves to.
        var s = new AppSettings { Engine = SttEngine.Cloud, SttProvider = AiProviders.CustomId, CloudSttBaseUrl = m.Root + "/openai/v1", CloudSttModel = "whisper-x", CloudSttKeyProtected = Secret.Protect("k"), Language = "da", AiProvider = AiProviders.NoneId };
        var t = CloudStt.Resolve(s);
        Check(lines, t.BaseUrl == m.Root + "/openai/v1" && t.Model == "whisper-x" && t.Key == "k", "Speech Custom: its own address, model and key");
        var groqSettings = new AppSettings { SttProvider = AiProviders.GroqId, AiProvider = AiProviders.NoneId };
        AiConfig.SetKey(groqSettings, AiProviders.GroqId, "gsk_shared");
        groqSettings.SttModels[AiProviders.GroqId] = "whisper-large-v3";
        var gt = CloudStt.Resolve(groqSettings);
        Check(lines, gt.Key == "gsk_shared" && gt.BaseUrl == AiProviders.Groq.SttBaseUrl && gt.Model == "whisper-large-v3" && Modes.CloudTranslateModel(groqSettings) == "whisper-large-v3"
                     && !AiConfig.IsConfigured(groqSettings),
            "Speech Groq shares the Groq key; translation model; the AI stays off with provider none");
        var mistralSettings = new AppSettings { SttProvider = AiProviders.MistralId, AiProvider = AiProviders.NoneId, Engine = SttEngine.Cloud, TranslateTarget = "en" };
        AiConfig.SetKey(mistralSettings, AiProviders.MistralId, "m");
        Check(lines, Modes.CloudTranslateModel(mistralSettings) == null && Modes.RouteFor(mistralSettings, false) == Modes.TranslateRoute.Unavailable,
            "Speech Mistral: no Whisper translation route");
        await Task.CompletedTask;
    }

    // ================================================================ (b) migration

    private static AppSettings Old(string json)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.Json)!;
        AiConfig.Migrate(s);
        return s;
    }

    private static void MigrationChecks(List<string> lines)
    {
        lines.Add("");
        lines.Add("== settings migration ==");
        string k = Secret.Protect("gsk_old"), k2 = Secret.Protect("sk-old"), sttKey = Secret.Protect("gsk_speech");

        var groq = Old($$"""{"LlmBaseUrl":"https://api.groq.com/openai/v1","LlmModel":"openai/gpt-oss-120b","LlmKeyProtected":"{{k}}","AiCleanup":true,"CloudSttBaseUrl":"https://api.groq.com/openai/v1","CloudSttModel":"whisper-large-v3","CloudSttKeyProtected":"{{sttKey}}","Engine":1}""");
        var gt = AiConfig.Target(groq);
        Check(lines, groq.AiProvider == "groq" && gt?.Key == "gsk_old" && gt.Model == "openai/gpt-oss-120b" && gt.BaseUrl == "https://api.groq.com/openai/v1" && AiConfig.IsConfigured(groq),
            $"Old Groq setup: provider groq, key and model kept ({gt?.Model})");
        var st = CloudStt.Resolve(groq);
        Check(lines, groq.SttProvider == "groq" && st.Model == "whisper-large-v3" && st.Key == "gsk_old" && st.BaseUrl == "https://api.groq.com/openai/v1",
            "Old Groq speech: provider groq, model kept, the one Groq key shared");

        var openai = Old($$"""{"LlmBaseUrl":"https://api.openai.com/v1","LlmModel":"gpt-5-mini","LlmKeyProtected":"{{k2}}"}""");
        var ot = AiConfig.Target(openai);
        Check(lines, openai.AiProvider == "custom" && ot?.BaseUrl == "https://api.openai.com/v1" && ot.Model == "gpt-5-mini" && ot.Key == "sk-old",
            "Old non-Groq address: Custom with the same address, model and key");
        Check(lines, OpenAiCompatibleAdapter.BuildPayload(ot!, new AiRequest("s", "u"), false)["reasoning_effort"] == null, "  ... and the same request as before (no new parameters)");

        var ollama = Old("""{"LlmBaseUrl":"http://localhost:11434/v1","LlmModel":"llama3.3","LlmKeyProtected":""}""");
        Check(lines, ollama.AiProvider == "custom" && AiConfig.IsConfigured(ollama) && AiConfig.Target(ollama)?.Key == "", "Old Ollama on this PC: Custom, set up without a key");

        var none = Old($$"""{"LlmBaseUrl":"https://api.groq.com/openai/v1","LlmModel":"llama-3.3-70b-versatile","LlmKeyProtected":"","CloudSttBaseUrl":"https://api.groq.com/openai/v1","CloudSttKeyProtected":"{{sttKey}}"}""");
        Check(lines, none.AiProvider == "none" && !AiConfig.IsConfigured(none) && AiConfig.Key(none, "groq") == "gsk_speech" && CloudStt.Resolve(none).Key == "gsk_speech",
            "Old setup with only a Groq speech key: AI stays off, the speech key becomes the Groq key");
        none.AiProvider = AiProviders.GroqId;
        Check(lines, !none.AiModels.ContainsKey("groq") && AiConfig.Target(none)?.Model == "openai/gpt-oss-120b",
            $"  ... the untouched old default model is not saved as a choice, so turning AI on gives the recommended model ({AiConfig.Target(none)?.Model})");
        var typedModel = Old("""{"LlmBaseUrl":"https://api.groq.com/openai/v1","LlmModel":"openai/gpt-oss-20b","LlmKeyProtected":""}""");
        Check(lines, typedModel.AiModels.GetValueOrDefault("groq") == "openai/gpt-oss-20b", "Old setup without a key but with a model the user picked: the model is kept");

        var otherStt = Old("""{"CloudSttBaseUrl":"https://api.openai.com/v1","CloudSttModel":"whisper-1","CloudSttKeyProtected":"x"}""");
        var ost = CloudStt.Resolve(otherStt);
        Check(lines, otherStt.SttProvider == "custom" && ost.BaseUrl == "https://api.openai.com/v1" && ost.Model == "whisper-1" && Modes.CloudTranslateModel(otherStt) == "whisper-1",
            "Old non-Groq speech address: Custom, as before");

        var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(groq, AppSettings.Json), AppSettings.Json)!;
        string before = JsonSerializer.Serialize(roundTrip, AppSettings.Json);
        AiConfig.Migrate(roundTrip);
        Check(lines, roundTrip.AiProvider == "groq" && AiConfig.Key(roundTrip, "groq") == "gsk_old" && before == JsonSerializer.Serialize(roundTrip, AppSettings.Json),
            "Saved and loaded again: unchanged, and migrating again does nothing");

        var lazy = new AppSettings { LlmKeyProtected = k };
        Check(lines, AiConfig.IsConfigured(lazy) && lazy.AiProvider == "groq", "A settings object built in code is migrated on first use");

        var switching = Old($$"""{"LlmKeyProtected":"{{k}}","LlmModel":"openai/gpt-oss-120b"}""");
        AiConfig.SetKey(switching, "openai", "sk-new");
        switching.AiProvider = "openai";
        Check(lines, AiConfig.Target(switching)?.Model == "gpt-6-luna" && AiConfig.Target(switching)?.Key == "sk-new", "Switching to OpenAI: its own key, the recommended model");
        switching.AiProvider = "groq";
        Check(lines, AiConfig.Target(switching)?.Key == "gsk_old" && AiConfig.Target(switching)?.Model == "openai/gpt-oss-120b", "Switching back to Groq: its key and model are still there");
        Check(lines, !switching.AiKeys.Values.Any(v => v.Contains("gsk_") || v.Contains("sk-")), "Keys are stored encrypted, never in plain text");

        // The real test data folder (MURMUR_DATA_DIR): an old settings file.
        if (AppSettings.IsTestInstance)
        {
            var loaded = AppSettings.Load();
            var raw = JsonNode.Parse(File.ReadAllText(Path.Combine(AppSettings.DataDir, "settings.json")));
            string oldKey = Secret.Unprotect(raw?["LlmKeyProtected"]?.GetValue<string>() ?? "");
            var lt = AiConfig.Target(loaded);
            Check(lines, lt != null && lt.Provider.Id == (raw?["AiProvider"]?.GetValue<string>() is { Length: > 0 } p ? p : "groq") && (raw?["AiProvider"] != null || lt.Key == oldKey),
                $"The test data folder's settings: provider {loaded.AiProvider}, model {lt?.Model}, speech {loaded.SttProvider} {CloudStt.ModelName(loaded)}");
        }
    }

    // ================================================================ (c) real Groq calls

    private static async Task RealGroqAsync(List<string> lines)
    {
        lines.Add("");
        lines.Add("== real calls to Groq ==");
        if (!AppSettings.IsTestInstance || Environment.GetEnvironmentVariable("MURMUR_PROVIDERTEST_REAL") == "0")
        {
            lines.Add("skipped (needs MURMUR_DATA_DIR; MURMUR_PROVIDERTEST_REAL=0 skips)");
            return;
        }
        var s = AppSettings.Load(); // never saved
        var target = AiConfig.Target(s);
        if (target == null || target.Provider.Id != AiProviders.GroqId) { lines.Add("skipped: the test settings have no Groq key"); return; }
        int calls = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string? problem = await AiService.TestAsync(target); calls++;
        Check(lines, problem == null, $"Test button against Groq {target.Model}: {problem ?? "OK"} in {watch.ElapsedMilliseconds} ms");

        watch.Restart();
        var llm = new LlmClient(s);
        string polished = await llm.CompleteAsync("Fix the grammar of the text in <transcript>. Return only the fixed text.", "<transcript>\nhej jeg hedder murmur og jeg skriver tekst\n</transcript>"); calls++;
        Check(lines, polished.Length > 10 && polished.Contains("Murmur", StringComparison.OrdinalIgnoreCase), $"Polish through LlmClient in {watch.ElapsedMilliseconds} ms: [{polished}]");

        if (Environment.GetEnvironmentVariable("MURMUR_PROVIDERTEST_REAL") == "min") { lines.Add($"{calls} real calls to Groq (MURMUR_PROVIDERTEST_REAL=min)"); return; }
        watch.Restart();
        string json = await AiCall.ChatAsync(s, "Answer with JSON: {\"intent\": string}.", "Pick intent 'reply' for: svar på mailen", json: true, maxSeconds: 15); calls++;
        Check(lines, AiCall.ParseJson(json)?["intent"]?.GetValue<string>() == "reply", $"Router-style JSON call through AiCall in {watch.ElapsedMilliseconds} ms: {json}");

        watch.Restart();
        var copy = new AppSettings();
        var ids = await AiService.RefreshModelsAsync(copy, target); calls++;
        Check(lines, ids.Contains("openai/gpt-oss-120b") && !ids.Any(i => i.Contains("whisper")), $"Groq's live model list in {watch.ElapsedMilliseconds} ms: {ids.Count} chat models ({string.Join(", ", ids.Take(8))})");
        var bad = await AiService.TestAsync(target with { Key = "gsk_not_a_real_key_000000" }); calls++;
        Check(lines, bad?.Contains("did not accept the key") == true, $"A wrong Groq key in plain words: \"{bad}\"");
        lines.Add($"{calls} real calls to Groq");
    }
}
