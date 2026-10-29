// Runs every library action against a real tenant, the way an ODC app would, before uploading the library.
// Usage: dotnet run --project McpTriage.Smoke -- <api-client.json> <tenant> <appKey> [question]
// The JSON file holds the ODC API client: { "client_id": "...", "client_secret": "..." }. Keep it outside the project.
using System.Text.Json.Nodes;
using McpTriage;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: dotnet run --project McpTriage.Smoke -- <api-client.json> <tenant> <appKey> [question]");
    return 2;
}
var (file, tenant, appKey) = (args[0], args[1], args[2]);
var question = args.Length > 3 ? args[3] : "In two or three sentences: what does this app do, and which screen is the entry point?";
var library = new McpTriageLibrary();
var client = JsonNode.Parse(File.ReadAllText(file));
var connection = new McpConnection
{
    TenantHostname = tenant,
    ClientId = client["client_id"].GetValue<string>(),
    ClientSecret = client["client_secret"].GetValue<string>(),
    AllowedAppKeys = appKey,
};

void ExpectError(string code, Action action)
{
    try
    {
        action();
    }
    catch (McpTriageException err) when (err.Code == code)
    {
        Console.WriteLine($"  ok: {err.Message[..Math.Min(200, err.Message.Length)]}");
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

Step("CheckConnection", () =>
{
    var info = library.CheckConnection(connection);
    Console.WriteLine($"  {info.TenantHostname} sees {info.Identity}; access token until {info.AccessTokenExpiresOn:u}");
});

Step("A wrong secret is refused", () =>
{
    ExpectError("invalid_client", () => library.CheckConnection(connection with { ClientSecret = "wrong-" + Guid.NewGuid() }));
});

Step("Apps outside AllowedAppKeys are refused", () =>
{
    ExpectError("app_not_allowed", () => library.GetAppLogs(connection with { AllowedAppKeys = Guid.NewGuid().ToString() }, appKey, "", 24, "", 1, ""));
    ExpectError("app_not_allowed", () => library.GetAppLogs(connection with { AllowedAppKeys = "" }, appKey, "", 24, "", 1, ""));
});

Step("ListElements screens, two pages", () =>
{
    var first = library.ListElements(connection, appKey, "screens", 2);
    Console.WriteLine($"  {first.Total} screens, page 1: {string.Join(", ", first.Elements.Select(e => e.Name))}, hasMore={first.HasMore}, nextOffset={first.NextOffset}");
    if (!first.HasMore) return;
    var second = library.ListElements(connection, appKey, "screens", 2, first.NextOffset);
    Console.WriteLine($"  page 2: {string.Join(", ", second.Elements.Select(e => e.Name))}");
    if (second.Elements.Any(e => first.Elements.Any(f => f.Key == e.Key))) throw new Exception("page 2 repeats page 1");
});

Step("SearchModel", () =>
{
    var result = library.SearchModel(connection, appKey, "municipality", "", 3);
    Console.WriteLine($"  {result.Total} matches: {string.Join(", ", result.Elements.Select(e => $"{e.ObjectType}:{e.Name}"))}, hasMore={result.HasMore}");
});

Step("GetAppLogs, two pages (Development)", () =>
{
    var first = library.GetAppLogs(connection, appKey, "", 168, "", 3, "");
    var newest = first.Entries.FirstOrDefault().Message ?? "";
    Console.WriteLine($"  {first.Total} errors/warnings in 7 days, hasMore={first.HasMore}; newest: {newest[..Math.Min(120, newest.Length)]}");
    if (!first.HasMore) return;
    var second = library.GetAppLogs(connection, appKey, "", 168, "", 3, "", first.NextOffset);
    if (second.Entries.Any(e => first.Entries.Any(f => f.LogId == e.LogId))) throw new Exception("page 2 repeats page 1");
    Console.WriteLine($"  page 2: {second.Entries.Count} entries, none repeated");
});

Step("GetAppTraces (Development)", () =>
{
    var result = library.GetAppTraces(connection, appKey, false, 168, 1000, 3, "");
    Console.WriteLine($"  {result.Total} requests over 1 s in 7 days, hasMore={result.HasMore}; " + string.Join("; ", result.Traces.Select(t => $"{t.SpanName} {t.DurationMs} ms")));
});

// Informational: depends on the API client's Monitoring permission in Production, not on the library
Console.WriteLine("== GetAppLogs (Production), informational");
try
{
    var prod = library.GetAppLogs(connection, appKey, "", 168, "", 1, "Production");
    Console.WriteLine($"  readable: {prod.Total} errors/warnings in 7 days");
}
catch (McpTriageException err)
{
    Console.WriteLine($"  not readable: [{err.Code}]");
}

Step("StartAnalysis + PollAnalysis", () =>
{
    var started = library.StartAnalysis(connection, appKey, question);
    Console.WriteLine($"  session {started.SessionId}, run {started.RunId}");
    var answer = "";
    var cursor = 0L;
    var replayChecked = false;
    for (var poll = 1; poll <= 60; poll++)
    {
        var progress = library.PollAnalysis(connection, started.SessionId, started.RunId, cursor, answer.Length > 0, 20);
        // As if the app had failed to save: the same cursor must give the same events again (and maybe more)
        if (!replayChecked && !progress.IsFinished && progress.NextCursor > cursor)
        {
            replayChecked = true;
            var again = library.PollAnalysis(connection, started.SessionId, started.RunId, cursor, answer.Length > 0, 1);
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
    library.CloseAnalysis(connection, started.SessionId);
    throw new Exception("No answer after 60 polls; conversation closed");
});

Step("Bad input is refused", () =>
{
    ExpectError("invalid_kind", () => library.ListElements(connection, appKey, "widgets", 5));
});

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
