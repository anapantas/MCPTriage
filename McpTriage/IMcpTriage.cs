using OutSystems.ExternalLibraries.SDK;

namespace McpTriage;

[OSInterface(Name = "McpTriage", Description = "Read-only analysis of ODC apps through the tenant's OutSystems MCP server: ask Mentor about an app, search its model, list its elements, read its logs and traces. It never changes or publishes an app. "
    + "How to use: 1) Sign in once with the library's signin tool and store the result in secret Settings and a one-row entity, with the allowed app keys and a TokenKey (README, recipe 1). "
    + "2) Call the actions only from Server Actions or Timers that check the user's role, never from the browser. "
    + "3) After every call, save the Tokens output when Tokens.Renewed is True; tokens are encrypted with TokenKey. "
    + "4) Analysis: StartAnalysis, then PollAnalysis from a Timer with Cursor = the previous NextCursor until IsFinished. "
    + "5) Lists: when HasMore is True, call again with Offset = NextOffset. "
    + "Every call stays within ODC's 95-second and 5.5 MB limits by returning early with where to continue. Errors start with a code in brackets, e.g. [sign_in_expired], [app_not_allowed].")]
public interface IMcpTriage
{
    [OSAction(Description = "Opens a Mentor conversation and asks the question about the app, with an instruction to analyse only and never change anything. Returns at once, while Mentor works. "
        + "How to use: save SessionId and RunId, then call PollAnalysis with Cursor 0 until IsFinished, ideally from a Timer. Save Tokens when Tokens.Renewed is True.",
        ReturnName = "Started", ReturnDescription = "The conversation and run ids to poll")]
    AnalysisStart StartAnalysis(
        [OSParameter(Description = "The stored sign-in, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "Asset key (GUID) of the app to analyse; must be in Connection.AllowedAppKeys")] string appKey,
        [OSParameter(Description = "The question, in any language, up to 8000 characters. Mentor answers in the same language.")] string question,
        [OSParameter(Description = "The sign-in after the call, encrypted: save it when Renewed is True. Keep it off screens.")] out McpTokens tokens);

    [OSAction(Description = "Returns what Mentor wrote after Cursor. Waits up to MaxWaitSeconds for news, but returns about 5 seconds after new answer text arrives, so the answer can be shown as it grows. Closes the conversation when the analysis is over. "
        + "How to use: start with Cursor 0; in one transaction append NewText to the answer and save NextCursor (and Tokens when Renewed); repeat with Cursor = NextCursor until IsFinished. "
        + "Polling again with the same Cursor returns that text again, so a failed save loses nothing.",
        ReturnName = "Progress", ReturnDescription = "New answer text and notes, the cursor to continue from, and whether the analysis is over")]
    AnalysisProgress PollAnalysis(
        [OSParameter(Description = "The stored sign-in, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "SessionId from StartAnalysis")] string sessionId,
        [OSParameter(Description = "RunId from StartAnalysis")] string runId,
        [OSParameter(Description = "Bookmark in Mentor's output: the position in the list of events Mentor writes during the run (notes, answer text, title) where this read starts. 0 on the first poll, then the NextCursor of the previous poll. Store it with the analysis.")] long cursor,
        [OSParameter(Description = "True when the answer you keep already has text, so a new message starts on a new paragraph")] bool answerStarted,
        [OSParameter(Description = "Longest wait for Mentor in this call, 1 to 60 seconds (0 = 8); it returns sooner when text arrives or the analysis ends. About 45 from Timers; 5 or less from screens, whose calls time out sooner.")] int maxWaitSeconds,
        [OSParameter(Description = "The sign-in after the call, encrypted: save it when Renewed is True. Keep it off screens.")] out McpTokens tokens);

    [OSAction(Description = "Closes a Mentor conversation, stopping any run in progress. Anything Mentor left unpublished is discarded. "
        + "How to use: only needed to give up on an analysis early; PollAnalysis closes it on its own when the analysis ends.",
        ReturnName = "Tokens", ReturnDescription = "The sign-in after the call, encrypted: save it when Renewed is True")]
    McpTokens CloseAnalysis(
        [OSParameter(Description = "The stored sign-in, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "SessionId from StartAnalysis")] string sessionId);

    [OSAction(Description = "Searches the app model by meaning or text, e.g. \"municipality\" finds the Municipality entity and the actions that use it. "
        + "How to use: show Name and Description of each element; when HasMore is True, call again with Offset = NextOffset. Save Tokens when Tokens.Renewed is True.",
        ReturnName = "Result", ReturnDescription = "A page of matching elements, with paging. Holds no tokens, so it can go to a screen.")]
    ModelElementList SearchModel(
        [OSParameter(Description = "The stored sign-in, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "Asset key (GUID) of the app; must be in Connection.AllowedAppKeys")] string appKey,
        [OSParameter(Description = "What to look for, in words or an element name")] string query,
        [OSParameter(Description = "Comma-separated object types to keep, e.g. Entities,Actions,Screens; empty for all")] string objectTypes,
        [OSParameter(Description = "Maximum results in this page, 1 to 100 (0 = 20)")] int limit,
        [OSParameter(Description = "Results to skip: 0 for the first page, then NextOffset")] int offset,
        [OSParameter(Description = "The sign-in after the call, encrypted: save it when Renewed is True. Keep it off screens.")] out McpTokens tokens);

    [OSAction(Description = "Lists one kind of element the app owns. "
        + "How to use: pick Kind; when HasMore is True, call again with Offset = NextOffset. Save Tokens when Tokens.Renewed is True.",
        ReturnName = "Result", ReturnDescription = "A page of elements, with paging. Holds no tokens, so it can go to a screen.")]
    ModelElementList ListElements(
        [OSParameter(Description = "The stored sign-in, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "Asset key (GUID) of the app; must be in Connection.AllowedAppKeys")] string appKey,
        [OSParameter(Description = "entities, actions, screens, structures, roles, themes, agents or connections")] string kind,
        [OSParameter(Description = "Maximum results in this page, 1 to 100 (0 = 50)")] int limit,
        [OSParameter(Description = "Results to skip: 0 for the first page, then NextOffset")] int offset,
        [OSParameter(Description = "The sign-in after the call, encrypted: save it when Renewed is True. Keep it off screens.")] out McpTokens tokens);

    [OSAction(Description = "Reads the app's runtime logs, newest first. "
        + "How to use: e.g. Severities \"Error\" and Hours 48 for recent errors; when HasMore is True, call again with the same filters and Offset = NextOffset. Save Tokens when Tokens.Renewed is True.",
        ReturnName = "Result", ReturnDescription = "A page of log entries, with paging. Holds no tokens, so it can go to a screen; show messages escaped.")]
    LogList GetAppLogs(
        [OSParameter(Description = "The stored sign-in, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "Asset key (GUID) of the app; must be in Connection.AllowedAppKeys")] string appKey,
        [OSParameter(Description = "Comma-separated: Debug, Information, Warning, Error (empty = Error,Warning)")] string severities,
        [OSParameter(Description = "How many hours back, up to 744 (0 = 24)")] int hours,
        [OSParameter(Description = "Text the message must contain; empty for any")] string search,
        [OSParameter(Description = "Maximum entries in this page, 1 to 500 (0 = 50)")] int limit,
        [OSParameter(Description = "Environment name (Development, Test, Production) or key; empty = Development")] string stage,
        [OSParameter(Description = "Entries to skip: 0 for the first page, then NextOffset")] int offset,
        [OSParameter(Description = "The sign-in after the call, encrypted: save it when Renewed is True. Keep it off screens.")] out McpTokens tokens);

    [OSAction(Description = "Reads the app's request traces (timings), newest first. "
        + "How to use: e.g. ErrorsOnly for failed requests, or MinDurationMs 2000 for slow ones; when HasMore is True, call again with the same filters and Offset = NextOffset. Save Tokens when Tokens.Renewed is True.",
        ReturnName = "Result", ReturnDescription = "A page of traces, with paging. Holds no tokens, so it can go to a screen.")]
    TraceList GetAppTraces(
        [OSParameter(Description = "The stored sign-in, from GetMcpConnection")] McpConnection connection,
        [OSParameter(Description = "Asset key (GUID) of the app; must be in Connection.AllowedAppKeys")] string appKey,
        [OSParameter(Description = "True to keep only failed requests")] bool errorsOnly,
        [OSParameter(Description = "How many hours back, up to 744 (0 = 24)")] int hours,
        [OSParameter(Description = "Keep only requests slower than this, in ms (0 = all)")] int minDurationMs,
        [OSParameter(Description = "Maximum traces in this page, 1 to 500 (0 = 50)")] int limit,
        [OSParameter(Description = "Environment name (Development, Test, Production) or key; empty = Development")] string stage,
        [OSParameter(Description = "Traces to skip: 0 for the first page, then NextOffset")] int offset,
        [OSParameter(Description = "The sign-in after the call, encrypted: save it when Renewed is True. Keep it off screens.")] out McpTokens tokens);

    [OSAction(Description = "Renews the tokens now. "
        + "How to use: call it from a daily Timer to keep the sign-in fresh and alert an admin when Tokens.RefreshTokenExpiresOn is near or the call fails with [sign_in_expired].",
        ReturnName = "Tokens", ReturnDescription = "The renewed sign-in, encrypted, and when it expires: save it")]
    McpTokens RenewTokens(
        [OSParameter(Description = "The stored sign-in, from GetMcpConnection")] McpConnection connection);
}
