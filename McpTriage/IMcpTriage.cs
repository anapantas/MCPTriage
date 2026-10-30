using OutSystems.ExternalLibraries.SDK;

namespace McpTriage;

[OSInterface(Name = "McpTriage", Description = "Read-only analysis of ODC apps through the tenant's OutSystems MCP server: ask Mentor about an app, search its model, list its elements, read its logs and traces. It never changes or publishes an app. "
    + "How to use: 1) In ODC Portal, create an API client with read-only permissions and keep its ID and secret in secret Settings, with the allowed app keys (README, recipe 1). No one has to sign in. "
    + "2) Call the actions only from Server Actions or Timers that check the user's role, never from the browser. "
    + "3) Analysis: StartAnalysis, then PollAnalysis from a Timer with Cursor = the previous NextCursor until IsFinished. "
    + "4) Lists: when HasMore is True, call again with Offset = NextOffset. "
    + "Every call stays within ODC's 95-second and 5.5 MB limits by returning early with where to continue. Errors start with a code in brackets, e.g. [app_not_allowed], [permission_denied].")]
public interface IMcpTriage
{
    [OSAction(Description = "Opens a Mentor conversation and asks the question about the app, with an instruction to analyse only and never change anything. Returns at once, while Mentor works. "
        + "How to use: save SessionId and RunId, then call PollAnalysis with Cursor 0 until IsFinished, ideally from a Timer.",
        ReturnName = "Started", ReturnDescription = "The conversation and run ids to poll")]
    AnalysisStart StartAnalysis(
        [OSParameter(Description = "The connection, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "Asset key (GUID) of the app to analyse; must be in Connection.AllowedAppKeys")] string appKey,
        [OSParameter(Description = "The question, in any language, up to 8000 characters. Mentor answers in the same language.")] string question);

    [OSAction(Description = "Returns what Mentor wrote after Cursor. Waits up to MaxWaitSeconds for news, but returns about 5 seconds after new answer text arrives, so the answer can be shown as it grows. Closes the conversation when the analysis is over. "
        + "How to use: start with Cursor 0; in one transaction append NewText to the answer and save NextCursor; repeat with Cursor = NextCursor until IsFinished. "
        + "Polling again with the same Cursor returns that text again, so a failed save loses nothing.",
        ReturnName = "Progress", ReturnDescription = "New answer text and notes, the cursor to continue from, and whether the analysis is over")]
    AnalysisProgress PollAnalysis(
        [OSParameter(Description = "The connection, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "SessionId from StartAnalysis")] string sessionId,
        [OSParameter(Description = "RunId from StartAnalysis")] string runId,
        [OSParameter(Description = "Bookmark in Mentor's output: the position in the list of events Mentor writes during the run (notes, answer text, title) where this read starts. 0 on the first poll, then the NextCursor of the previous poll. Store it with the analysis.")] long cursor,
        [OSParameter(Description = "True when the answer you keep already has text, so a new message starts on a new paragraph")] bool answerStarted,
        [OSParameter(Description = "Longest wait for Mentor in this call, 1 to 60 seconds (0 = 8); it returns sooner when text arrives or the analysis ends. About 45 from Timers; 5 or less from screens, whose calls time out sooner.")] int maxWaitSeconds);

    [OSAction(Description = "Closes a Mentor conversation, stopping any run in progress. Anything Mentor left unpublished is discarded. "
        + "How to use: only needed to give up on an analysis early; PollAnalysis closes it on its own when the analysis ends.")]
    void CloseAnalysis(
        [OSParameter(Description = "The connection, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "SessionId from StartAnalysis")] string sessionId);

    [OSAction(Description = "Searches the app model by meaning or text, e.g. \"municipality\" finds the Municipality entity and the actions that use it. "
        + "How to use: show Name and Description of each element; when HasMore is True, call again with Offset = NextOffset.",
        ReturnName = "Result", ReturnDescription = "A page of matching elements, most relevant first, with paging")]
    ModelElementList SearchModel(
        [OSParameter(Description = "The connection, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "Asset key (GUID) of the app; must be in Connection.AllowedAppKeys")] string appKey,
        [OSParameter(Description = "What to look for, in words or an element name")] string query,
        [OSParameter(Description = "Comma-separated object types to keep, e.g. Entities,Actions,Screens; empty for all")] string objectTypes,
        [OSParameter(Description = "Maximum results in this page, 1 to 100 (0 = 20)")] int limit,
        [OSParameter(Description = "Results to skip: 0 for the first page, then NextOffset")] int offset = 0);

    [OSAction(Description = "Lists one kind of element the app owns, sorted by name. "
        + "How to use: pick Kind; when HasMore is True, call again with Offset = NextOffset.",
        ReturnName = "Result", ReturnDescription = "A page of elements, sorted by name, with paging")]
    ModelElementList ListElements(
        [OSParameter(Description = "The connection, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "Asset key (GUID) of the app; must be in Connection.AllowedAppKeys")] string appKey,
        [OSParameter(Description = "entities, actions, screens, structures, roles, themes, agents or connections")] string kind,
        [OSParameter(Description = "Maximum results in this page, 1 to 100 (0 = 50)")] int limit,
        [OSParameter(Description = "Results to skip: 0 for the first page, then NextOffset")] int offset = 0);

    [OSAction(Description = "Reads the app's runtime logs, newest first. "
        + "How to use: e.g. Severities \"Error\" and Hours 48 for recent errors; when HasMore is True, call again with the same filters and Offset = NextOffset. "
        + "Only Development is readable: ODC doesn't let API clients read logs and traces in Test or Production, which fail with [permission_denied].",
        ReturnName = "Result", ReturnDescription = "A page of log entries, with paging. Show messages escaped: they are untrusted text.")]
    LogList GetAppLogs(
        [OSParameter(Description = "The connection, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "Asset key (GUID) of the app; must be in Connection.AllowedAppKeys")] string appKey,
        [OSParameter(Description = "Comma-separated: Debug, Information, Warning, Error (empty = Error,Warning)")] string severities,
        [OSParameter(Description = "How many hours back, up to 744 (0 = 24)")] int hours,
        [OSParameter(Description = "Text the message must contain; empty for any")] string search,
        [OSParameter(Description = "Maximum entries in this page, 1 to 500 (0 = 50)")] int limit,
        [OSParameter(Description = "Environment name or key; empty = Development. Only Development is readable with an API client.")] string stage,
        [OSParameter(Description = "Entries to skip: 0 for the first page, then NextOffset")] int offset = 0);

    [OSAction(Description = "Reads the app's request traces (timings), newest first. "
        + "How to use: e.g. ErrorsOnly for failed requests, or MinDurationMs 2000 for slow ones; when HasMore is True, call again with the same filters and Offset = NextOffset. "
        + "Only Development is readable: ODC doesn't let API clients read logs and traces in Test or Production, which fail with [permission_denied].",
        ReturnName = "Result", ReturnDescription = "A page of traces, with paging")]
    TraceList GetAppTraces(
        [OSParameter(Description = "The connection, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "Asset key (GUID) of the app; must be in Connection.AllowedAppKeys")] string appKey,
        [OSParameter(Description = "True to keep only failed requests")] bool errorsOnly,
        [OSParameter(Description = "How many hours back, up to 744 (0 = 24)")] int hours,
        [OSParameter(Description = "Keep only requests slower than this, in ms (0 = all)")] int minDurationMs,
        [OSParameter(Description = "Maximum traces in this page, 1 to 500 (0 = 50)")] int limit,
        [OSParameter(Description = "Environment name or key; empty = Development. Only Development is readable with an API client.")] string stage,
        [OSParameter(Description = "Traces to skip: 0 for the first page, then NextOffset")] int offset = 0);

    [OSAction(Description = "Checks that the API client can reach the tenant's MCP server. "
        + "How to use: call it from a daily Timer and alert an admin when it fails, e.g. with [invalid_client] after the secret was regenerated or the client deleted.",
        ReturnName = "Info", ReturnDescription = "Who the MCP server sees and when the current access token expires")]
    ConnectionInfo CheckConnection(
        [OSParameter(Description = "The connection, from GetMcpConnection")] McpConnection connection);
}
