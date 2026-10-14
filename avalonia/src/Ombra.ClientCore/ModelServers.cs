using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace Ombra.ClientCore;

/// <summary>
/// A model server omp knows how to use on this computer, as omp 18.8.0 sets it up when nothing in models.yml names it
/// (config/model-registry.ts <c>#addImplicitDiscoverableProviders</c>; vLLM: pi-catalog's default discovery URL):
/// its provider id, wire protocol, discovery type and address (the environment variable omp reads, else the default).
/// </summary>
public sealed record LocalModelServer(string Id, string Name, string Api, string Discovery, string BaseUrl, string? EnvironmentVariable)
{
    /// <summary>The models.yml entry that makes omp use the server at <paramref name="baseUrl"/>: no key, models discovered.</summary>
    public ModelProviderEntry Entry(string baseUrl) => new()
    {
        Id = Id,
        BaseUrl = baseUrl.Trim(),
        Api = Api,
        NoAuth = true,
        Discovery = Discovery,
    };

    /// <summary>Where the server lists its models: Ollama's own <c>/api/tags</c>, the others' OpenAI-style <c>/v1/models</c>.</summary>
    public string ModelsUrl(string baseUrl) =>
        Discovery == "ollama" ? ModelServerProbe.Root(baseUrl) + "/api/tags" : ModelServerProbe.WithV1(baseUrl) + "/models";

    /// <summary>Ollama, LM Studio, llama.cpp and vLLM at the addresses omp would use.</summary>
    public static IReadOnlyList<LocalModelServer> Defaults(Func<string, string?> env) =>
    [
        new("ollama", "Ollama", ModelApis.Responses, "ollama",
            Env(env, "OLLAMA_BASE_URL") ?? OllamaHost(Env(env, "OLLAMA_HOST")) ?? "http://127.0.0.1:11434",
            Env(env, "OLLAMA_BASE_URL") is not null ? "OLLAMA_BASE_URL" : Env(env, "OLLAMA_HOST") is not null ? "OLLAMA_HOST" : null),
        new("lm-studio", "LM Studio", ModelApis.ChatCompletions, "lm-studio", Env(env, "LM_STUDIO_BASE_URL") ?? "http://127.0.0.1:1234/v1",
            Env(env, "LM_STUDIO_BASE_URL") is not null ? "LM_STUDIO_BASE_URL" : null),
        new("llama.cpp", "llama.cpp", ModelApis.Responses, "llama.cpp", Env(env, "LLAMA_CPP_BASE_URL") ?? "http://127.0.0.1:8080",
            Env(env, "LLAMA_CPP_BASE_URL") is not null ? "LLAMA_CPP_BASE_URL" : null),
        new("vllm", "vLLM", ModelApis.ChatCompletions, "openai-models-list", "http://127.0.0.1:8000/v1", null),
    ];

    private static string? Env(Func<string, string?> env, string name) => env(name)?.Trim() is { Length: > 0 } v ? v : null;

    /// <summary>OLLAMA_HOST as omp reads it (model-discovery.ts normalizeOllamaHostEnv): "host", ":port", "//host" or a URL; http gets port 11434 when none is given.</summary>
    internal static string? OllamaHost(string? value)
    {
        if (value is null) return null;
        var candidate = value.Contains("://", StringComparison.Ordinal) ? value
            : value.StartsWith("//", StringComparison.Ordinal) ? "http:" + value
            : value.StartsWith(':') ? "http://127.0.0.1" + value
            : "http://" + value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0) return null;
        var authority = candidate[(candidate.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0];
        var explicitPort = authority.LastIndexOf(':') > authority.LastIndexOf(']');
        if (explicitPort) return $"{u.Scheme}://{u.Host}:{u.Port}";
        return u.Scheme == "http" ? $"http://{u.Host}:11434" : $"{u.Scheme}://{u.Host}";
    }
}

/// <summary>What a model server answered when asked for its models.</summary>
/// <param name="Running">It answered at all (a refusal or an error page included).</param>
/// <param name="Models">The model ids it listed; null when it gave no model list.</param>
/// <param name="Problem">What went wrong, in words; null when it listed its models.</param>
/// <param name="Status">The HTTP status, when there was one.</param>
public sealed record ModelListAnswer(bool Running, IReadOnlyList<string>? Models, string? Problem, int? Status = null)
{
    public bool Ok => Models is not null;
}

/// <summary>
/// Asks a model server for its models, the way omp's discovery does (GET with the key as a bearer token), from the app
/// itself and off the UI thread: the providers page shows which local servers run and lets a custom provider be tried
/// before it is saved. Loopback addresses are asked directly (never through a proxy).
/// </summary>
public static class ModelServerProbe
{
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(15);

    private static readonly HttpClient Direct = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = LocalTimeout }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly HttpClient Proxied = new(new SocketsHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>GETs <paramref name="url"/> and reads an OpenAI-style (<c>{"data":[{"id"}]}</c>) or Ollama (<c>{"models":[{"name"}]}</c>) model list.</summary>
    public static async Task<ModelListAnswer> ListAsync(string url, IReadOnlyDictionary<string, string>? headers = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new(false, null, "The address is not an http:// or https:// URL.");
        var loopback = IsLoopback(uri);
        var wait = timeout ?? (loopback ? LocalTimeout : RemoteTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(wait);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            foreach (var (k, v) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(k, v);
            using var response = await (loopback ? Direct : Proxied).SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(true, null, status switch
                {
                    401 or 403 => $"The server refused the key (HTTP {status}).",
                    404 => $"No model list at {url} (HTTP 404).",
                    _ => $"The server answered HTTP {status}" + (Snippet(body) is { Length: > 0 } s ? ": " + s : "."),
                }, status);
            var models = ParseModels(body);
            return models is null
                ? new(true, null, $"{url} did not answer with a model list.", status)
                : new(true, models, null, status);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, null, $"No answer within {wait.TotalSeconds:0.#} s.");
        }
        catch (HttpRequestException e)
        {
            var refused = e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused };
            return new(false, null, refused ? "Nothing answers at " + uri.GetLeftPart(UriPartial.Authority) + "." : "Couldn't connect: " + e.Message);
        }
    }

    /// <summary>The ids of an OpenAI-style model list (<c>data[].id</c>) or of Ollama's tags (<c>models[].name|model</c>); null when the JSON is neither.</summary>
    public static IReadOnlyList<string>? ParseModels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                return [.. data.EnumerateArray().Select(m => Str(m, "id")).OfType<string>()];
            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                return [.. models.EnumerateArray().Select(m => Str(m, "name") ?? Str(m, "model")).OfType<string>()];
            return null;
        }
        catch (JsonException) { return null; }

        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
    }

    private static string Snippet(string body)
    {
        var t = body.ReplaceLineEndings(" ").Trim();
        return t.Length > 200 ? t[..200] + "…" : t;
    }

    public static bool IsLoopback(Uri u) =>
        u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);

    /// <summary>scheme://host[:port] of <paramref name="baseUrl"/> (Ollama's API sits at the root).</summary>
    public static string Root(string baseUrl) =>
        Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : baseUrl.Trim().TrimEnd('/');

    /// <summary>The base URL ending in /v1 (omp's normalizeOpenAIModelsListBaseUrl): query and fragment dropped, "/v1" appended unless it is there.</summary>
    public static string WithV1(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var u)) return baseUrl.Trim().TrimEnd('/') + "/v1";
        var path = u.AbsolutePath.TrimEnd('/');
        return u.GetLeftPart(UriPartial.Authority) + (path.EndsWith("/v1", StringComparison.Ordinal) ? path : path + "/v1");
    }

    /// <summary>The base URL as written, without trailing slashes, query or fragment (omp's normalizeBareDiscoveryBaseUrl).</summary>
    public static string Bare(string baseUrl) =>
        Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) + u.AbsolutePath.TrimEnd('/') : baseUrl.Trim().TrimEnd('/');
}
