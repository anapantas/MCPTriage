# McpTriage architecture

## How it works

An admin creates an ODC API client once in ODC Portal; the ODC app keeps its ID and secret in secret Settings and passes them to every action. Each action gets an access token for the API client (client credentials), opens its own connection to the tenant's MCP server, calls one or a few tools, and returns the result. No person signs in and nothing is stored. The flow an app follows to use the actions is in the README ("How an app uses the library").

```mermaid
flowchart LR
    subgraph Portal["Once, in ODC Portal"]
        AC["API client<br/>read-only permissions"]
    end

    subgraph App["ODC app, server side"]
        SET[/"Settings<br/>McpTenant, McpClientId,<br/>McpClientSecret, McpAllowedAppKeys"/]
        SA["Server Actions and Timers<br/>role check, GetMcpConnection"]
        DB[("Entity<br/>Analysis")]
    end

    subgraph Lib["McpTriage external library: one call ends within 95 s and 5.5 MB"]
        IF["IMcpTriage<br/>8 actions, descriptions"]
        LIB["McpTriageLibrary<br/>allowed apps, validate,<br/>call tools, map, page, cut"]
        LIM["Limits<br/>80 s budget, 4.5 MB lists"]
        CL["McpClient<br/>JSON-RPC over HTTPS,<br/>access token in memory"]
    end

    subgraph Tenant["Tenant, https://tenant"]
        TOK["/auth/realms/…/token<br/>tenant identity provider"]
        MCP["/mcp<br/>OutSystems MCP server"]
        MEN["Mentor"]
        CTX["Context service<br/>app model"]
        MON["Monitoring<br/>logs and traces"]
    end

    AC -. "client ID and secret, copied by hand" .-> SET
    SET --> SA
    SA <--> DB
    SA -- "McpConnection + inputs" --> IF
    IF -- "result" --> SA
    IF --> LIB --> CL
    LIB -.- LIM
    CL -- "client_credentials grant" --> TOK
    CL -- "initialize, tools/call" --> MCP
    MCP -- "mentor_* tools" --> MEN
    MCP -- "context_* tools" --> CTX
    MCP -- "app_logs, app_traces" --> MON
```

One action call, from the library's side:

```mermaid
sequenceDiagram
    participant App as ODC app
    participant Lib as McpTriage
    participant Tok as Token endpoint
    participant MCP as /mcp

    App->>Lib: action(McpConnection, inputs)
    Note over Lib: 80 s budget starts<br/>app key checked against AllowedAppKeys
    opt no access token in memory, or under 5 min left
        Lib->>Tok: client_credentials grant
        Tok-->>Lib: access token (hours)
    end
    Lib->>MCP: initialize, notifications/initialized
    opt 401: access token refused
        Lib->>Tok: client_credentials grant, once
        Lib->>MCP: same request again
    end
    Lib->>MCP: tools/call, one or more
    MCP-->>Lib: JSON or event stream
    Note over Lib: keep what fits in 5.5 MB, cut long texts
    Lib->>MCP: DELETE the MCP session
    Lib-->>App: result
```

## Tenets

1. **Read-only toward the analysed app.** Every Mentor question carries an analysis-only instruction, with the user's question fenced in `<request>` tags it can't close (`McpTriageLibrary.cs`, `AnalysisOnly`, `RequestTag`). Any publish event, whatever its case, cancels the run (`ReadPage`), and a finished or failed analysis always closes the conversation (`TryClose`), which discards unpublished edits. No action calls a publish or deploy tool, and `ListElements` reaches only the `context_*` tools in `ElementKinds`. The API client's read-only permissions in ODC Portal are the control underneath all of this.
2. **Stateless between calls.** ODC may reuse the execution context or not, so static state holds only what can be fetched again: the token endpoint per tenant and the access token per API client (`McpClient.cs`, `TokenEndpoints`, `AccessTokens`). Mentor conversation and run ids and the cursor travel in and out of every action.
3. **No person, no stored credential.** The library signs in as an ODC API client with the client credentials grant (`McpClient.GetToken`). It returns no token, stores nothing, and keeps the access token only in memory, keyed by a hash so the secret itself is never kept.
4. **Business actions, lean structures.** One action per use (`IMcpTriage.cs`), each returning only the fields a triage app needs, plus `DetailsJson` where the server's row is richer than the structure.
5. **Errors carry a stable code.** `McpTriageException` puts `[code]` first in the message, because ODC only passes the message on. A missing permission in ODC Portal becomes `[permission_denied]`, a refused API client `[invalid_client]`.
6. **Every call fits ODC's limits on its own.** One time budget per call (`Limits.CallTime`, enforced in `McpClient.Send` and `GetToken`), lists trimmed to `Limits.ListBytes` (`Limits.Fit`), text cut to the attribute's length (`Len`, `Limits.Cut`). When something doesn't fit, the action returns early with where to continue (`NextCursor`, `NextOffset`) instead of failing.
7. **The secret never leaves the tenant's host.** Requests go only to `https://<TenantHostname>` (checked to be a bare hostname), redirects are never followed (`McpClient.Http`, `AllowAutoRedirect = false`), and the token endpoint from the tenant's OpenID configuration is used only when it is HTTPS on that same host (`TokenEndpoint`). The secret and tokens are never logged or put in error messages. What the app must add (role checks, allowed apps, secret Settings) is in the README, "Security".
8. **Self-describing in ODC Studio.** Every action, parameter, structure and attribute has a description; actions and structures that need a pattern from the caller say how to use them.

## Design decisions

| Decision | Why | Rejected alternative |
|---|---|---|
| Sign in as an ODC API client (client credentials against the tenant's identity provider) | The MCP server accepts tokens from the tenant's identity provider (its protected-resource metadata names it), so a token for an API client works, Mentor included (checked). Nothing expires after hours, no one signs in, and the client's Portal permissions limit what it can do | A stored person's sign-in through `/mcp/token` (the first design, commit `e9c220c`): it ended with the browser session, about 10 hours, was cut off whenever the same person signed in again, needed an npm tool to set up, and acted with that person's full rights |
| Access token cached in memory per API client, renewed 5 minutes before it expires or on a 401 | Tokens last hours; asking on every call adds a round trip. Memory only, so nothing leaks into the app's data | Storing the token in the app: a credential in the database for no real gain. Asking on every call: slower |
| A new MCP connection per action (`McpClient` is created and disposed inside each action) | Mentor conversations are reachable from any connection of the same identity (checked: a conversation opened on one connection was read and closed from another), and the execution context isn't guaranteed to survive between calls | Passing the MCP session id between calls: saves a few round trips, but adds a value the app must store and a failure mode when the server forgets the session |
| Plain `HttpClient` with JSON-RPC, no MCP SDK | The protocol needed here is small: initialize, tools/call, JSON or event-stream answers. Fewer dependencies keep the zip small and the runtime predictable | The MCP C# SDK: more features than needed, a larger dependency surface |
| `PollAnalysis` waits up to `maxWaitSeconds` (max 60, and never past the call budget) and returns only new text | The platform limit is 95 s per call, and a Mentor run takes minutes, so an analysis is always several calls. Timers can wait long, screens can't, so the caller decides. Returning deltas keeps each payload small | One blocking "analyse" action: breaks the 95 s limit on any non-trivial question |
| `PollAnalysis` returns `TextWindow` (5 s) after the first new answer text instead of waiting out `maxWaitSeconds` | The app can only show text it has saved, so a 45 s wait would show the answer in 45 s jumps. 5 s keeps it nearly live without opening a new MCP connection for every chunk | Returning on the first chunk: a poll, a connection and a commit every 2 s while Mentor writes |
| `PollAnalysis` always passes an explicit cursor to `mentor_get_run` and returns `NextCursor`; a page of events is taken whole or not at all; once the run is over (no `nextCursor` from the server) the position is counted from the events read | A read without a cursor marks events delivered, so text lost between the library and the app's commit could never be read again. With the cursor, a repeat poll returns the same text (checked across connections) | Reading without a cursor (the server tracks delivery): one fewer value for the app to store, but any failed save loses part of the answer |
| One 80 s budget shared by all HTTP traffic of a call, enforced by cancelling the request and closing a blocking event stream | 95 s is a hard limit; a slow MCP answer must end the call cleanly, with a `[time_limit]` code, before ODC kills it | A per-request timeout: several slow requests in one call can still add up past 95 s |
| Lists measured with every non-ASCII character escaped, kept under 4.5 MB | ODC counts the serialized inputs and outputs, and its serializer isn't documented; the worst case is safe whatever it does | Counting characters: a page of accented or non-Latin log messages could pass 5.5 MB |
| Halve the page when the MCP server answers `context_response_too_large` | The server's own transport limit depends on the rows' content, so no fixed page size is always safe | Failing: the app would have to guess a smaller `limit` |
| `ListElements` reads the whole set (up to 500), sorts it by name and pages it itself | The server returned the same app's screens in a different order seconds apart, so its pages repeated and skipped elements (seen in the smoke test) | Passing `offset` to the server: pages overlap |
| Close the conversation automatically when an analysis ends | Guarantees unpublished edits are discarded even if the app forgets `CloseAnalysis` | Leaving it to the app: one missed call leaves a conversation, and any attempted edit, open. The cost: the final poll can't be repeated |
| Text attribute lengths set once, in `Len` (`Structures.cs`), and used both in the attributes and when cutting values | The SDK's default length is 50 characters, and a length declared in one place can't drift from the one enforced | Defaults: answers and messages would be declared far too short |
| No action returns the whole model | ImpactMap's model is about 10 MB and the platform limit is 5.5 MB per call | Paging the model: it is one opaque graph, not useful to an app in pieces |
| No automatic redirects | A 307 or 308 answer would make `HttpClient` resend the request body, the client secret included, to whatever host it names | Following redirects: nothing the tenant's servers need |
| The token endpoint from the tenant's OpenID configuration is used only when it is HTTPS on the tenant's own host | The client secret is the most sensitive value the library handles; a configuration pointing elsewhere must not be able to collect it | Trusting the configuration as is: one bad answer, or a tampered one, sends the secret to another host |
| The user's question goes inside `<request>` tags, with those tags removed from the question, after rules telling Mentor to take it only as a question | The question is free text from an app user, and Mentor acts as the API client; fencing makes "ignore the rules and publish" read as quoted text | Sending the question as is: any instruction in it carries the same weight as the library's rules. Fencing is a mitigation, not a guarantee: a read-only API client is the real control |
| `AllowedAppKeys` is mandatory; `*` allows every app, empty allows none | The app decides which apps its users may read, and the library enforces it on every app-scoped action, so one Server Action that forgets a check can't open the whole tenant | An optional list where empty means all: the safe setting would be the one people forget |
| Only one return value per action, no `out` parameters | ODC's SDK documents one output per action; with no tokens to hand back there is nothing else to return | Tokens in an `out` parameter (the previous design): no longer needed |
