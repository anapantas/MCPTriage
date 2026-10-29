using OutSystems.ExternalLibraries.SDK;

namespace McpTriage;

// Text attributes default to 50 characters in the SDK, so every long value states its length.
// The library cuts values to these same lengths (Limits.Cut), so each length is set once, here.
internal static class Len
{
    public const int Hostname = 255;
    public const int ClientId = 4000;
    public const int ClientSecret = 500;
    public const int AllowedAppKeys = 4000;
    public const int Id = 50;
    public const int Status = 20;
    public const int AnswerText = 1_000_000;
    public const int Steps = 100_000;
    public const int Title = 500;
    public const int ErrorCode = 100;
    public const int ErrorMessage = 4000;
    public const int ObjectType = 100;
    public const int Key = 100;
    public const int Name = 500;
    public const int Description = 4000;
    public const int DetailsJson = 200_000;
    public const int Severity = 20;
    public const int LogMessage = 20_000;
    public const int Category = 200;
    public const int SpanType = 100;
    public const int SpanName = 1000;
}

[OSStructure(Description = "How to reach the tenant's MCP server as an ODC API client. How to use: build it in one Server Action (GetMcpConnection) from the McpTenant, McpClientId, McpClientSecret and McpAllowedAppKeys Settings, and pass it as Connection to every action. Never show it on a screen or send it to the browser.")]
public struct McpConnection
{
    [OSStructureField(Description = "Tenant hostname without https:// or a path, e.g. mycompany.outsystems.dev", Length = Len.Hostname, IsMandatory = true)]
    public string TenantHostname;

    [OSStructureField(Description = "Client ID of the ODC API client (ODC Portal > API clients). Keep it in a secret Setting.", Length = Len.ClientId, IsMandatory = true)]
    public string ClientId;

    [OSStructureField(Description = "Client secret of the ODC API client. Keep it only in a secret Setting; regenerating it in ODC Portal means updating that Setting.", Length = Len.ClientSecret, IsMandatory = true)]
    public string ClientSecret;

    [OSStructureField(Description = "The apps this connection may read: asset keys separated by commas, or * for every app the API client can see. Actions on any other app fail with [app_not_allowed]. Keep it in a Setting.", Length = Len.AllowedAppKeys, IsMandatory = true)]
    public string AllowedAppKeys;
}

[OSStructure(Description = "Whether the API client can reach the tenant's MCP server. How to use: call CheckConnection from a daily Timer and alert an admin when it fails.")]
public struct ConnectionInfo
{
    [OSStructureField(Description = "Tenant the MCP server answered for", Length = Len.Hostname)]
    public string TenantHostname;

    [OSStructureField(Description = "Who the MCP server sees: the API client's ID", Length = Len.Key)]
    public string Identity;

    [OSStructureField(Description = "When the current access token expires (UTC). Informational: the library gets a new one on its own.")]
    public DateTime AccessTokenExpiresOn;
}

[OSStructure(Description = "A Mentor analysis that has started. How to use: save SessionId and RunId with the analysis, then call PollAnalysis with Cursor 0.")]
public struct AnalysisStart
{
    [OSStructureField(Description = "Mentor conversation id: pass it to PollAnalysis and CloseAnalysis", Length = Len.Id)]
    public string SessionId;

    [OSStructureField(Description = "Mentor run id: pass it to PollAnalysis", Length = Len.Id)]
    public string RunId;
}

[OSStructure(Description = "What Mentor produced since Cursor. How to use: in one transaction, append NewText to the answer you keep and save NextCursor; then, unless IsFinished, call PollAnalysis again with Cursor = NextCursor. If saving fails, polling again with the old Cursor returns that text again, so nothing is lost or repeated.")]
public struct AnalysisProgress
{
    [OSStructureField(Description = "working, succeeded, failed, cancelled or not_found", Length = Len.Status)]
    public string Status;

    [OSStructureField(Description = "True when the analysis is over; the library has then closed the conversation, so stop polling")]
    public bool IsFinished;

    [OSStructureField(Description = "Answer text from Cursor up to NextCursor: append it to the answer you keep. Empty when Mentor wrote nothing new yet.", Length = Len.AnswerText)]
    public string NewText;

    [OSStructureField(Description = "Mentor's progress notes from Cursor up to NextCursor, one per line, to show what it is doing", Length = Len.Steps)]
    public string NewSteps;

    [OSStructureField(Description = "Bookmark after the events returned in this call: where the next read starts. Pass it as Cursor to the next PollAnalysis, and save it in the same transaction as NewText.")]
    public long NextCursor;

    [OSStructureField(Description = "Title Mentor gave the conversation, when it set one in this part of the run", Length = Len.Title)]
    public string Title;

    [OSStructureField(Description = "True when Mentor tried to change the app in spite of the analysis-only instruction. Nothing was published; the change was discarded with the conversation.")]
    public bool ChangeAttempted;

    [OSStructureField(Description = "True when Mentor started a publish and the library cancelled the run")]
    public bool PublishBlocked;

    [OSStructureField(Description = "True when the library closed the Mentor conversation")]
    public bool Closed;

    [OSStructureField(Description = "Why the run did not succeed, e.g. publish_blocked or mentor_failed; empty otherwise", Length = Len.ErrorCode)]
    public string ErrorCode;

    [OSStructureField(Description = "Mentor's or the library's explanation of ErrorCode", Length = Len.ErrorMessage)]
    public string ErrorMessage;
}

[OSStructure(Description = "One element of an app model: entity, action, screen, structure, role, theme, agent or connection. How to use: show Name and Description; parse DetailsJson with JSONDeserialize when you need more, such as an entity's attributes.")]
public struct ModelElement
{
    [OSStructureField(Description = "entities, actions, screens, structures, roles, themes, agents or connections", Length = Len.ObjectType)]
    public string ObjectType;

    [OSStructureField(Description = "Element key in the app model", Length = Len.Key)]
    public string Key;

    [OSStructureField(Description = "Element name, e.g. Municipality", Length = Len.Name)]
    public string Name;

    [OSStructureField(Description = "Description written in the app, if any", Length = Len.Description)]
    public string Description;

    [OSStructureField(Description = "The full row as JSON, e.g. an entity's attributes or a screen's roles. The server shortens values over about 2 KB.", Length = Len.DetailsJson)]
    public string DetailsJson;
}

[OSStructure(Description = "A page of model elements. How to use: while HasMore is True and you need more, call the same action again with Offset = NextOffset.")]
public struct ModelElementList
{
    [OSStructureField(Description = "The elements in this page")]
    public List<ModelElement> Elements;

    [OSStructureField(Description = "How many elements match in total")]
    public int Total;

    [OSStructureField(Description = "True when more elements exist after this page")]
    public bool HasMore;

    [OSStructureField(Description = "Offset of the next page: pass it as Offset to get more")]
    public int NextOffset;
}

[OSStructure(Description = "A runtime log entry of the app")]
public struct LogEntry
{
    [OSStructureField(Description = "When it was written (UTC)")]
    public DateTime Time;

    [OSStructureField(Description = "Debug, Information, Warning or Error", Length = Len.Severity)]
    public string Severity;

    [OSStructureField(Description = "The log message, with the stack trace for errors", Length = Len.LogMessage)]
    public string Message;

    [OSStructureField(Description = "Where it came from, e.g. the module or VersionsManager", Length = Len.Category)]
    public string Category;

    [OSStructureField(Description = "Revision of the app that wrote the entry")]
    public int AppRevision;

    [OSStructureField(Description = "Log entry id", Length = Len.Key)]
    public string LogId;
}

[OSStructure(Description = "A page of runtime log entries, newest first. How to use: while HasMore is True and you need more, call GetAppLogs again with the same filters and Offset = NextOffset.")]
public struct LogList
{
    [OSStructureField(Description = "The log entries in this page, newest first")]
    public List<LogEntry> Entries;

    [OSStructureField(Description = "How many entries match in total")]
    public int Total;

    [OSStructureField(Description = "True when more entries exist after this page")]
    public bool HasMore;

    [OSStructureField(Description = "Offset of the next page: pass it as Offset to get more")]
    public int NextOffset;
}

[OSStructure(Description = "A request trace: one server request or screen data fetch with its timing")]
public struct TraceEntry
{
    [OSStructureField(Description = "Trace id", Length = Len.Key)]
    public string TraceId;

    [OSStructureField(Description = "When the request started (UTC)")]
    public DateTime StartTime;

    [OSStructureField(Description = "Ok or Error", Length = Len.Status)]
    public string Status;

    [OSStructureField(Description = "How long it took, in milliseconds", Decimals = 2)]
    public decimal DurationMs;

    [OSStructureField(Description = "Kind of request, e.g. a screen data action or a server action", Length = Len.SpanType)]
    public string SpanType;

    [OSStructureField(Description = "What ran, e.g. MainFlow.WhatIsMissing.GetTarget", Length = Len.SpanName)]
    public string SpanName;

    [OSStructureField(Description = "How many steps (spans) the request had")]
    public int NumberOfSpans;

    [OSStructureField(Description = "How many of those steps failed")]
    public int NumberOfErrors;
}

[OSStructure(Description = "A page of request traces, newest first. How to use: while HasMore is True and you need more, call GetAppTraces again with the same filters and Offset = NextOffset.")]
public struct TraceList
{
    [OSStructureField(Description = "The traces in this page, newest first")]
    public List<TraceEntry> Traces;

    [OSStructureField(Description = "How many traces match in total")]
    public int Total;

    [OSStructureField(Description = "True when more traces exist after this page")]
    public bool HasMore;

    [OSStructureField(Description = "Offset of the next page: pass it as Offset to get more")]
    public int NextOffset;
}
