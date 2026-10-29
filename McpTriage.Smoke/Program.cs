// Runs every library action against a real tenant, the way an ODC app would, before uploading the library.
// Usage: dotnet run --project McpTriage.Smoke -- <sign-in.json> <tenant> <appKey> [question]
// The sign-in file is the one `npm run login` writes in ../signin (~/.impactmap-triage-api/odc-library.json).
// Renewed tokens are written back to it, as the ODC app would save them, but decrypted, so the file stays usable by npm run tokens.
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using McpTriage;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: dotnet run --project McpTriage.Smoke -- <sign-in.json> <tenant> <appKey> [question]");
    return 2;
}
var (file, tenant, appKey) = (args[0], args[1], args[2]);
var question = args.Length > 3 ? args[3] : "In two or three sentences: what does this app do, and which screen is the entry point?";
var library = new McpTriageLibrary();
// A fresh TokenKey per run, as an app would keep one in a secret Setting
var tokenKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

McpConnection Connection()
{
    var stored = JsonNode.Parse(File.ReadAllText(file));
    return new McpConnection
    {
        TenantHostname = tenant,
        ClientId = stored["client"]["client_id"].GetValue<string>(),
        AccessToken = stored["tokens"]?["access_token"]?.GetValue<string>(),
        RefreshToken = stored["tokens"]["refresh_token"].GetValue<string>(),
        AllowedAppKeys = appKey,
        TokenKey = tokenKey,
    };
}

void Save(McpTokens tokens)
{
    if (!tokens.Renewed) return;
    var key = TokenSeal.Key(tokenKey);
    var stored = JsonNode.Parse(File.ReadAllText(file));
    stored["tokens"]["access_token"] = TokenSeal.Open(key, nameof(McpTokens.AccessToken), tokens.AccessToken);
    stored["tokens"]["refresh_token"] = TokenSeal.Open(key, nameof(McpTokens.RefreshToken), tokens.RefreshToken);
    stored["savedAt"] = DateTime.UtcNow.ToString("O");
    File.WriteAllText(file, stored.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"  (tokens renewed; sign-in valid until {tokens.RefreshTokenExpiresOn:u})");
}

void ExpectError(string code, Action action)
{
    try
    {
        action();
    }
    catch (McpTriageException err) when (err.Code == code)
    {
        Console.WriteLine($"  ok: {err.Message}");
        return;
    }
    throw new Exception($"expected [{code}]");
}

var failures = 0;
void Step(string name, Action action)
{
    Console.WriteLine($"== {name}");
    try
    {
        action();
    }
    catch (Exception err)
    {
        failures++;
        Console.WriteLine($"  FAILED: {err.Message}");
    }
}

Step("RenewTokens", () =>
{
    var tokens = library.RenewTokens(Connection());
    Save(tokens);
    if (!tokens.AccessToken.StartsWith("mcpt1.") || !tokens.RefreshToken.StartsWith("mcpt1.")) throw new Exception("tokens came back unencrypted");
    Console.WriteLine($"  access token until {tokens.AccessTokenExpiresOn:u}, sign-in until {tokens.RefreshTokenExpiresOn:u}; tokens encrypted");
});

Step("Encrypted tokens work as input; a wrong TokenKey is refused", () =>
{
    library.ListElements(Connection(), appKey, "roles", 1, 0, out var first);
    Save(first);
    var encrypted = Connection() with { AccessToken = first.AccessToken, RefreshToken = first.RefreshToken };
    var result = library.ListElements(encrypted, appKey, "roles", 1, 0, out var second);
    Save(second);
    Console.WriteLine($"  call with encrypted tokens: {result.Total} roles");
    ExpectError("token_key_mismatch", () => library.ListElements(encrypted with { TokenKey = new string('x', 40) }, appKey, "roles", 1, 0, out _));
});

Step("Apps outside AllowedAppKeys are refused", () =>
{
    ExpectError("app_not_allowed", () => library.GetAppLogs(Connection() with { AllowedAppKeys = Guid.NewGuid().ToString() }, appKey, "", 24, "", 1, "", 0, out _));
    ExpectError("app_not_allowed", () => library.GetAppLogs(Connection() with { AllowedAppKeys = "" }, appKey, "", 24, "", 1, "", 0, out _));
});

Step("ListElements screens, two pages", () =>
{
    var first = library.ListElements(Connection(), appKey, "screens", 2, 0, out var tokens);
    Save(tokens);
    Console.WriteLine($"  {first.Total} screens, page 1: {string.Join(", ", first.Elements.Select(e => e.Name))}, hasMore={first.HasMore}, nextOffset={first.NextOffset}");
    if (!first.HasMore) return;
    var second = library.ListElements(Connection(), appKey, "screens", 2, first.NextOffset, out tokens);
    Save(tokens);
    Console.WriteLine($"  page 2: {string.Join(", ", second.Elements.Select(e => e.Name))}");
    if (second.Elements.Any(e => first.Elements.Any(f => f.Key == e.Key))) throw new Exception("page 2 repeats page 1");
});

Step("SearchModel", () =>
{
    var result = library.SearchModel(Connection(), appKey, "municipality", "", 3, 0, out var tokens);
    Save(tokens);
    Console.WriteLine($"  {result.Total} matches: {string.Join(", ", result.Elements.Select(e => $"{e.ObjectType}:{e.Name}"))}, hasMore={result.HasMore}");
});

Step("GetAppLogs, two pages", () =>
{
    var first = library.GetAppLogs(Connection(), appKey, "", 168, "", 3, "", 0, out var tokens);
    Save(tokens);
    var newest = first.Entries.FirstOrDefault().Message ?? "";
    Console.WriteLine($"  {first.Total} errors/warnings in 7 days, hasMore={first.HasMore}; newest: {newest[..Math.Min(120, newest.Length)]}");
    if (!first.HasMore) return;
    var second = library.GetAppLogs(Connection(), appKey, "", 168, "", 3, "", first.NextOffset, out tokens);
    Save(tokens);
    if (second.Entries.Any(e => first.Entries.Any(f => f.LogId == e.LogId))) throw new Exception("page 2 repeats page 1");
    Console.WriteLine($"  page 2: {second.Entries.Count} entries, none repeated");
});

Step("GetAppTraces", () =>
{
    var result = library.GetAppTraces(Connection(), appKey, false, 168, 1000, 3, "", 0, out var tokens);
    Save(tokens);
    Console.WriteLine($"  {result.Total} requests over 1 s in 7 days, hasMore={result.HasMore}; " + string.Join("; ", result.Traces.Select(t => $"{t.SpanName} {t.DurationMs} ms")));
});

Step("StartAnalysis + PollAnalysis", () =>
{
    var started = library.StartAnalysis(Connection(), appKey, question, out var tokens);
    Save(tokens);
    Console.WriteLine($"  session {started.SessionId}, run {started.RunId}");
    var answer = "";
    var cursor = 0L;
    var replayChecked = false;
    for (var poll = 1; poll <= 60; poll++)
    {
        var progress = library.PollAnalysis(Connection(), started.SessionId, started.RunId, cursor, answer.Length > 0, 20, out tokens);
        Save(tokens);
        // As if the app had failed to save: the same cursor must give the same events again (and maybe more)
        if (!replayChecked && !progress.IsFinished && progress.NextCursor > cursor)
        {
            replayChecked = true;
            var again = library.PollAnalysis(Connection(), started.SessionId, started.RunId, cursor, answer.Length > 0, 1, out tokens);
            Save(tokens);
            if (again.NextCursor < progress.NextCursor
                || !again.NewText.StartsWith(progress.NewText, StringComparison.Ordinal)
                || !again.NewSteps.StartsWith(progress.NewSteps, StringComparison.Ordinal))
                throw new Exception($"polling cursor {cursor} again gave different events (nextCursor {again.NextCursor} vs {progress.NextCursor})");
            Console.WriteLine($"  replay from cursor {cursor}: same events again (nextCursor {again.NextCursor}, +{again.NewText.Length - progress.NewText.Length} newer chars)");
            progress = again;
        }
        answer += progress.NewText;
        cursor = progress.NextCursor;
        Console.WriteLine($"  poll {poll}: {progress.Status}, +{progress.NewText.Length} chars, nextCursor={progress.NextCursor}, finished={progress.IsFinished}, closed={progress.Closed}, changeAttempted={progress.ChangeAttempted}, error={progress.ErrorCode}");
        if (progress.IsFinished)
        {
            Console.WriteLine($"  title: {progress.Title}\n--- answer ---\n{answer}\n--------------");
            if (progress.Status != "succeeded") throw new Exception(progress.ErrorMessage);
            return;
        }
    }
    library.CloseAnalysis(Connection(), started.SessionId);
    throw new Exception("No answer after 60 polls; conversation closed");
});

Step("Bad input is refused", () =>
{
    ExpectError("invalid_kind", () => library.ListElements(Connection(), appKey, "widgets", 5, 0, out _));
});

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
