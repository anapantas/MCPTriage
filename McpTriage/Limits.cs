using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpTriage;

// ODC stops an external library call after 95 s and refuses one whose inputs plus outputs pass 5.5 MB.
// The library keeps every call inside both on its own: it returns early with the place to continue from
// (NextCursor, NextOffset) instead of failing.
internal static class Limits
{
    // Time all MCP traffic of one call may take; the rest of the 95 s covers a cold start and ODC's own work.
    public static readonly TimeSpan CallTime = TimeSpan.FromSeconds(80);

    // Time PollAnalysis keeps free at the end of a call for one last read and closing the conversation.
    public static readonly TimeSpan PollReserve = TimeSpan.FromSeconds(15);

    // Rows of one list, measured as JSON with every non-ASCII character escaped (the worst case), well under 5.5 MB.
    public const int ListBytes = 4_500_000;

    // Largest MCP answer the library reads; the tenant's answers are far smaller, and the cap keeps a broken one
    // from exhausting the library's 1 GB of memory
    public const int AnswerChars = 32_000_000;

    private static readonly JsonSerializerOptions Measure = new() { IncludeFields = true };

    public static string Cut(string text, int length)
    {
        if (text == null || text.Length <= length) return text;
        var keep = length - 1;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
        return text[..keep] + "…";
    }

    // The rows from the start of the page that fit in ListBytes; always at least one, which Cut already bounds.
    public static List<T> Fit<T>(IEnumerable<T> rows)
    {
        var kept = new List<T>();
        var used = 0L;
        foreach (var row in rows)
        {
            used += JsonSerializer.SerializeToUtf8Bytes(row, Measure).Length;
            if (used > ListBytes && kept.Count > 0) break;
            kept.Add(row);
        }
        return kept;
    }

    // Where the next page starts, under whichever name the MCP tool uses; null when there is no next page.
    public static int? NextOffset(JsonNode answer)
    {
        var next = answer?["next_offset"] ?? answer?["nextPageOffset"] ?? answer?["next_page_offset"] ?? answer?["pagination"]?["nextPageOffset"];
        return next == null || next.GetValueKind() == JsonValueKind.Null ? null : (int)Json.Long(next);
    }
}
