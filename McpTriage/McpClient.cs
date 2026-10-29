using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McpTriage;

// Errors reach ODC as the exception message, so each starts with a stable code the app can test: "[code] message".
public sealed class McpTriageException : Exception
{
    public string Code { get; }

    public McpTriageException(string code, string message) : base($"[{code}] {message}")
    {
        Code = code;
    }
}

// One connection to https://<tenant>/mcp for the length of one library call: MCP over HTTP (JSON-RPC),
// with the OAuth access token renewed from the refresh token when the server answers 401.
// All its traffic shares one time budget (Limits.CallTime), so a call ends before ODC's 95 s limit.
internal sealed class McpClient : IDisposable
{
    private const string ProtocolVersion = "2025-06-18";
    // No redirects: a 307/308 would resend the refresh token or the bearer to wherever the answer points
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Limits.CallTime };
    private static readonly ConcurrentDictionary<string, string> TokenEndpoints = new();
    private static readonly Regex Hostname = new("^[A-Za-z0-9.-]+$");
    internal static readonly DateTime NullDate = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _tenant;
    private readonly string _clientId;
    private readonly byte[] _tokenKey;
    private string _accessToken;
    private string _refreshToken;
    private bool _renewed;
    private string _sessionId;
    private string _protocolVersion;
    private bool _initialized;
    private int _nextId = 1;

    public McpClient(McpConnection connection)
    {
        _tenant = connection.TenantHostname?.Trim();
        if (string.IsNullOrEmpty(_tenant) || !Hostname.IsMatch(_tenant))
            throw new McpTriageException("invalid_connection", "TenantHostname must be a hostname such as mycompany.outsystems.dev, without https:// or a path");
        if (string.IsNullOrWhiteSpace(connection.ClientId) || string.IsNullOrWhiteSpace(connection.RefreshToken))
            throw new McpTriageException("invalid_connection", "ClientId and RefreshToken are required");
        _clientId = connection.ClientId.Trim();
        _tokenKey = TokenSeal.Key(connection.TokenKey);
        _accessToken = TokenSeal.Open(_tokenKey, nameof(McpTokens.AccessToken), connection.AccessToken?.Trim());
        _refreshToken = TokenSeal.Open(_tokenKey, nameof(McpTokens.RefreshToken), connection.RefreshToken.Trim());
    }

    private Uri McpUri => new($"https://{_tenant}/mcp");

    // Time left in this call's budget
    public TimeSpan Remaining => Limits.CallTime - _clock.Elapsed;

    // Always encrypted with the app's TokenKey: the app stores and passes back only encrypted tokens
    public McpTokens Tokens => new()
    {
        Renewed = _renewed,
        AccessToken = TokenSeal.Seal(_tokenKey, nameof(McpTokens.AccessToken), _accessToken),
        RefreshToken = TokenSeal.Seal(_tokenKey, nameof(McpTokens.RefreshToken), _refreshToken),
        AccessTokenExpiresOn = JwtExpiry(_accessToken),
        RefreshTokenExpiresOn = JwtExpiry(_refreshToken),
    };

    // Calls one tool and returns its answer: the parsed JSON of its first text block.
    public JsonNode CallTool(string name, JsonObject arguments)
    {
        if (!_initialized) Initialize();
        var result = Send(Request("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments ?? new JsonObject() }), true);
        var texts = (result?["content"] as JsonArray ?? new JsonArray())
            .Where(c => Json.Str(c?["type"]) == "text")
            .Select(c => Json.Str(c["text"]) ?? "")
            .ToList();
        if (result?["isError"]?.GetValueKind() == JsonValueKind.True)
        {
            var message = string.Join("\n", texts);
            var code = Regex.Match(message, @"\[code: ([^\]]+)\]\s*$");
            throw new McpTriageException(code.Success ? code.Groups[1].Value : "tool_error", $"{name}: {Shorten(message)}");
        }
        if (texts.Count == 0) return null;
        try
        {
            return JsonNode.Parse(texts[0]);
        }
        catch (JsonException)
        {
            return JsonValue.Create(texts[0]);
        }
    }

    public void ForceRenew() => Renew();

    // Ends the MCP session on the server; failures are irrelevant, the server expires it anyway.
    public void Dispose()
    {
        if (_sessionId == null || Remaining < TimeSpan.FromSeconds(1)) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, McpUri);
            AddHeaders(request);
            using var timeout = new CancellationTokenSource(Min(Remaining, TimeSpan.FromSeconds(5)));
            using var response = Http.Send(request, timeout.Token);
        }
        catch
        {
            // ignored
        }
    }

    private JsonObject Request(string method, JsonObject parameters) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = _nextId++, ["method"] = method, ["params"] = parameters };

    private void Initialize()
    {
        var result = Send(Request("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "McpTriage ODC library", ["version"] = "1.0.0" },
        }), true);
        _protocolVersion = Json.Str(result?["protocolVersion"]) ?? ProtocolVersion;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, false);
        _initialized = true;
    }

    private JsonNode Send(JsonObject message, bool expectResponse)
    {
        // Each recovery is tried once per message, independently: a renewed token, then a new MCP session
        var renewed = false;
        var reconnected = false;
        if (string.IsNullOrEmpty(_accessToken))
        {
            Renew();
            renewed = true;
        }
        var isInitialize = Json.Str(message["method"]) == "initialize";
        for (;;)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, McpUri)
            {
                Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            AddHeaders(request);
            using var timeout = Timeout();
            try
            {
                using var response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                // Reading an event stream blocks, so the time limit ends it by closing the response
                using var stop = timeout.Token.Register(response.Dispose);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (renewed) throw new McpTriageException("sign_in_expired", "The MCP server refused the renewed token. Sign in again and save the new tokens.");
                    Renew();
                    renewed = true;
                    continue;
                }
                // The server forgot our MCP session: start a new one
                if (response.StatusCode == HttpStatusCode.NotFound && _sessionId != null && !isInitialize && !reconnected)
                {
                    _sessionId = null;
                    Initialize();
                    reconnected = true;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new McpTriageException($"mcp_http_{(int)response.StatusCode}", Shorten(ReadAll(response)));

                if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _sessionId = ids.First();
                return expectResponse ? ReadResult(response, message["id"]) : null;
            }
            catch (Exception err) when (timeout.IsCancellationRequested && err is not McpTriageException)
            {
                throw TimeUp();
            }
        }
    }

    private CancellationTokenSource Timeout()
    {
        var left = Remaining;
        if (left <= TimeSpan.Zero) throw TimeUp();
        return new CancellationTokenSource(left);
    }

    private static McpTriageException TimeUp() =>
        new("time_limit", "Stopped before ODC's 95-second limit for one call. Call the action again.");

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (_sessionId != null) request.Headers.Add("Mcp-Session-Id", _sessionId);
        if (_protocolVersion != null) request.Headers.Add("MCP-Protocol-Version", _protocolVersion);
    }

    // The answer comes either as plain JSON or as a server-sent event stream that carries it.
    private static JsonNode ReadResult(HttpResponseMessage response, JsonNode id)
    {
        var wanted = id?.ToJsonString();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            using var reader = new StreamReader(response.Content.ReadAsStream());
            var data = new StringBuilder();
            for (string line; (line = reader.ReadLine()) != null;)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart());
                    if (data.Length > Limits.AnswerChars) throw TooLarge();
                    continue;
                }
                if (line.Length > 0 || data.Length == 0) continue;
                var message = JsonNode.Parse(data.ToString()) as JsonObject;
                data.Clear();
                if (message?["id"]?.ToJsonString() == wanted) return Unwrap(message);
            }
            if (data.Length > 0 && JsonNode.Parse(data.ToString()) is JsonObject last && last["id"]?.ToJsonString() == wanted)
                return Unwrap(last);
            throw new McpTriageException("mcp_no_response", "The MCP server closed the stream without an answer");
        }
        return Unwrap(JsonNode.Parse(ReadAll(response)) as JsonObject);
    }

    private static JsonNode Unwrap(JsonObject message)
    {
        if (message == null) throw new McpTriageException("mcp_bad_response", "The MCP server sent an answer that is not JSON-RPC");
        if (message["error"] is JsonObject error)
            throw new McpTriageException("mcp_error", Shorten(Json.Str(error["message"]) ?? error.ToJsonString()));
        return message["result"];
    }

    private void Renew()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint())
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken,
                ["client_id"] = _clientId,
                ["resource"] = McpUri.ToString(),
            }),
        };
        using var timeout = Timeout();
        HttpStatusCode status;
        string body;
        try
        {
            using var response = Http.Send(request, timeout.Token);
            status = response.StatusCode;
            body = ReadAll(response);
        }
        catch (Exception err) when (timeout.IsCancellationRequested && err is not McpTriageException)
        {
            throw TimeUp();
        }
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            // e.g. "refresh token issued before the client session started": someone signed in again with this client
            var reason = TryParse(body) is JsonObject error ? Json.Str(error["error_description"]) ?? Json.Str(error["error"]) : null;
            throw new McpTriageException("sign_in_expired", "The stored sign-in can no longer be renewed: it ends with the browser sign-in session, or when someone signs in again with the same client. "
                + $"Sign in again and save the new tokens.{(reason == null ? "" : $" Server: {Shorten(reason)}")}");
        }
        if ((int)status is < 200 or > 299)
            throw new McpTriageException("token_renewal_failed", $"HTTP {(int)status}: {Shorten(body)}");

        var json = JsonNode.Parse(body);
        _accessToken = Json.Str(json?["access_token"]) ?? throw new McpTriageException("token_renewal_failed", "No access_token in the answer");
        var refresh = Json.Str(json?["refresh_token"]);
        if (!string.IsNullOrEmpty(refresh)) _refreshToken = refresh;
        _renewed = true;
    }

    // The token endpoint comes from the tenant's OAuth metadata; /mcp/token is what the tenants publish today.
    // Only a discovered endpoint is kept, so a slow or failed discovery is tried again on a later call.
    // The refresh token goes only to https on the tenant's own host, whatever the metadata says.
    private string TokenEndpoint()
    {
        if (TokenEndpoints.TryGetValue(_tenant, out var known)) return known;
        try
        {
            using var timeout = new CancellationTokenSource(Min(Remaining, TimeSpan.FromSeconds(10)));
            using var response = Http.Send(new HttpRequestMessage(HttpMethod.Get, $"https://{_tenant}/.well-known/oauth-authorization-server/mcp"), timeout.Token);
            if (response.IsSuccessStatusCode && Json.Str(JsonNode.Parse(ReadAll(response))?["token_endpoint"]) is { } endpoint
                && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, _tenant, StringComparison.OrdinalIgnoreCase))
                return TokenEndpoints[_tenant] = endpoint;
        }
        catch
        {
            // fall back below
        }
        return $"https://{_tenant}/mcp/token";
    }

    internal static DateTime JwtExpiry(string token)
    {
        try
        {
            var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var exp = JsonNode.Parse(Convert.FromBase64String(payload))?["exp"]?.GetValue<long>();
            return exp is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : NullDate;
        }
        catch
        {
            return NullDate;
        }
    }

    private static JsonNode TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadAll(HttpResponseMessage response)
    {
        using var reader = new StreamReader(response.Content.ReadAsStream());
        var buffer = new char[81920];
        var text = new StringBuilder();
        for (int read; (read = reader.Read(buffer, 0, buffer.Length)) > 0;)
        {
            if (text.Length + read > Limits.AnswerChars) throw TooLarge();
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    private static McpTriageException TooLarge() =>
        new("response_too_large", "The MCP server's answer was too large to read. Ask for less, e.g. a smaller limit.");

    private static string Shorten(string text) => text == null ? "" : text.Length <= 1500 ? text : text[..1500] + "…";
}

internal static class Json
{
    public static string Str(JsonNode node) => node switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.Null => null,
        _ => node.ToJsonString(),
    };

    public static long Long(JsonNode node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<long>()
        : long.TryParse(Str(node), out var parsed) ? parsed : 0;

    public static decimal Dec(JsonNode node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<decimal>() : 0m;

    public static bool True(JsonNode node) => node?.GetValueKind() == JsonValueKind.True;

    public static DateTime Date(JsonNode node) =>
        DateTime.TryParse(Str(node), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : McpClient.NullDate;

    // First array in an answer, whatever the tool calls it ("data", "results"...)
    public static JsonArray Rows(JsonNode node) =>
        node as JsonArray ?? (node as JsonObject)?.Select(p => p.Value).OfType<JsonArray>().FirstOrDefault() ?? new JsonArray();
}
