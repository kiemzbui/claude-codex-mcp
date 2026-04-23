using System.Text;
using ClaudeCodexMcp.Domain;

namespace ClaudeCodexMcp.Storage;

public static class OutputStoreBudget
{
    private const string TruncationMarker = "\n[truncated]";

    public static string NormalizeFormat(string? format) =>
        string.IsNullOrWhiteSpace(format) ? "json" : format.Trim().ToLowerInvariant();

    public static bool IsSupportedFormat(string? format)
    {
        var normalized = NormalizeFormat(format);
        return normalized is "json" or "text" or "jsonl";
    }

    public static CodexReadOutputResponse CreateReadOutputResponse(
        string jobId,
        string? threadId,
        string? turnId,
        string? agentId,
        int requestedOffset,
        int requestedLimit,
        string format,
        OutputLogPage page,
        string logRef,
        IReadOnlyList<OutputArtifactRef> artifactRefs,
        IReadOnlyList<ToolError> errors)
    {
        var normalizedFormat = NormalizeFormat(format);
        var entries = page.Entries.Select(entry => TruncateEntry(entry, OutputResponseLimits.PaginatedChunkBytes / 2)).ToArray();
        var entriesTruncated = page.Entries.Zip(entries, WasEntryTruncated).Any(truncated => truncated);
        var hasMore = !page.EndOfLog;
        var response = new CodexReadOutputResponse
        {
            JobId = jobId,
            ThreadId = string.IsNullOrWhiteSpace(threadId) ? null : threadId.Trim(),
            TurnId = string.IsNullOrWhiteSpace(turnId) ? null : turnId.Trim(),
            AgentId = string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim(),
            Offset = requestedOffset,
            Limit = requestedLimit,
            Format = normalizedFormat,
            Entries = normalizedFormat == "json" ? entries : [],
            Text = normalizedFormat switch
            {
                "text" => RenderText(entries),
                "jsonl" => RenderJsonLines(entries),
                _ => null
            },
            Truncated = entriesTruncated,
            NextOffset = hasMore ? page.NextOffset : null,
            EndOfOutput = page.EndOfLog,
            LogRef = logRef,
            ArtifactRefs = artifactRefs,
            Errors = errors
        };

        return EnforceReadOutputBudget(response);
    }

    public static CodexReadOutputResponse EnforceReadOutputBudget(CodexReadOutputResponse response) =>
        EnforceReadOutputBudget(response, OutputResponseLimits.PaginatedChunkBytes);

    public static CodexReadOutputResponse EnforceReadOutputBudget(
        CodexReadOutputResponse response,
        int maxBytes)
    {
        maxBytes = NormalizeMaxBytes(maxBytes);
        var originalEntries = response.Entries.ToArray();
        var entries = originalEntries.Select(entry => TruncateEntry(entry, maxBytes / 2)).ToList();
        var entriesTruncated = originalEntries.Zip(entries, WasEntryTruncated).Any(truncated => truncated);
        var text = TruncateUtf8(response.Text, maxBytes / 2, out var textTruncated);
        var result = response with
        {
            Entries = entries,
            Text = text,
            Truncated = response.Truncated || entriesTruncated || textTruncated
        };

        while (SerializedByteCount(result) > maxBytes && entries.Count > 1)
        {
            entries.RemoveAt(entries.Count - 1);
            result = result with
            {
                Entries = entries.ToArray(),
                Truncated = true,
                EndOfOutput = false,
                NextOffset = response.Offset + entries.Count,
                NextCursor = $"offset:{response.Offset + entries.Count}"
            };
        }

        while (SerializedByteCount(result) > maxBytes && entries.Count == 1)
        {
            var shrunk = ShrinkEntry(entries[0]);
            if (shrunk == entries[0])
            {
                break;
            }

            entries[0] = shrunk;
            result = result with
            {
                Entries = entries.ToArray(),
                Truncated = true,
                NextCursor = "truncated-string-field"
            };
        }

        while (SerializedByteCount(result) > maxBytes && !string.IsNullOrEmpty(text))
        {
            var nextBudget = Math.Max(Encoding.UTF8.GetByteCount(text) / 2, 1);
            text = TruncateUtf8(text, nextBudget, out _);
            result = result with
            {
                Text = text,
                Truncated = true,
                NextCursor = "truncated-string-field"
            };
        }

        if (SerializedByteCount(result) <= maxBytes)
        {
            return result;
        }

        return result with
        {
            Entries = [],
            Text = null,
            Truncated = true,
            EndOfOutput = false,
            NextOffset = response.Offset,
            NextCursor = $"offset:{response.Offset}"
        };
    }

    public static CodexResultResponse EnforceResultBudget(
        CodexResultResponse response,
        int maxBytes)
    {
        maxBytes = NormalizeMaxBytes(maxBytes);
        var summary = TruncateUtf8(response.Summary, OutputResponseLimits.SummaryBytes / 2, out var summaryTruncated);
        var fullOutput = TruncateUtf8(response.FullOutput, maxBytes / 2, out var outputTruncated);
        var result = response with
        {
            Summary = summary,
            FullOutput = fullOutput,
            Truncated = response.Truncated || summaryTruncated || outputTruncated
        };

        while (SerializedByteCount(result) > maxBytes && !string.IsNullOrEmpty(fullOutput))
        {
            var nextBudget = Math.Max(Encoding.UTF8.GetByteCount(fullOutput) / 2, 1);
            fullOutput = TruncateUtf8(fullOutput, nextBudget, out _);
            result = result with
            {
                FullOutput = fullOutput,
                Truncated = true,
                NextCursor = response.NextCursor ?? "truncated-string-field"
            };
        }

        while (SerializedByteCount(result) > maxBytes && !string.IsNullOrEmpty(summary))
        {
            var nextBudget = Math.Max(Encoding.UTF8.GetByteCount(summary) / 2, 1);
            summary = TruncateUtf8(summary, nextBudget, out _);
            result = result with
            {
                Summary = summary,
                Truncated = true,
                NextCursor = response.NextCursor ?? "truncated-string-field"
            };
        }

        return SerializedByteCount(result) <= maxBytes
            ? result
            : result with
            {
                FullOutput = null,
                Truncated = true,
                NextCursor = response.NextCursor ?? "truncated-string-field"
            };
    }

    public static int SerializedByteCount<T>(T value) =>
        Encoding.UTF8.GetByteCount(StorageJson.Serialize(value));

    public static string? TruncateUtf8(string? value, int maxBytes, out bool truncated)
    {
        truncated = false;
        if (value is null)
        {
            return null;
        }

        if (maxBytes <= 0)
        {
            truncated = value.Length > 0;
            return truncated ? TruncationMarker : value;
        }

        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        truncated = true;
        var markerBytes = Encoding.UTF8.GetByteCount(TruncationMarker);
        var prefixBudget = Math.Max(maxBytes - markerBytes, 0);
        if (prefixBudget == 0)
        {
            return TruncationMarker;
        }

        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, mid)) <= prefixBudget)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        var prefixLength = low;
        if (prefixLength > 0 && char.IsHighSurrogate(value[prefixLength - 1]))
        {
            prefixLength--;
        }

        return value[..prefixLength] + TruncationMarker;
    }

    private static int NormalizeMaxBytes(int maxBytes) =>
        Math.Clamp(maxBytes, 1, OutputResponseLimits.AbsoluteHardCapBytes);

    private static OutputLogEntry ShrinkEntry(OutputLogEntry entry)
    {
        var messageBytes = Encoding.UTF8.GetByteCount(entry.Message);
        var payloadBytes = Encoding.UTF8.GetByteCount(entry.PayloadJson ?? string.Empty);
        if (messageBytes >= payloadBytes && messageBytes > Encoding.UTF8.GetByteCount(TruncationMarker))
        {
            return entry with
            {
                Message = TruncateUtf8(entry.Message, messageBytes / 2, out _) ?? string.Empty
            };
        }

        if (payloadBytes > Encoding.UTF8.GetByteCount(TruncationMarker))
        {
            return entry with
            {
                PayloadJson = TruncateUtf8(entry.PayloadJson, payloadBytes / 2, out _)
            };
        }

        return entry;
    }

    private static OutputLogEntry TruncateEntry(OutputLogEntry entry, int maxStringBytes) => entry with
    {
        JobId = TruncateRequired(entry.JobId, 512),
        ThreadId = TruncateUtf8(entry.ThreadId, 512, out _),
        TurnId = TruncateUtf8(entry.TurnId, 512, out _),
        AgentId = TruncateUtf8(entry.AgentId, 512, out _),
        Source = TruncateRequired(entry.Source, 512),
        Level = TruncateRequired(entry.Level, 128),
        Message = TruncateRequired(entry.Message, Math.Max(maxStringBytes, 1024)),
        PayloadJson = TruncateUtf8(entry.PayloadJson, Math.Max(maxStringBytes, 1024), out _)
    };

    private static string TruncateRequired(string value, int maxBytes) =>
        TruncateUtf8(value, maxBytes, out _) ?? string.Empty;

    private static bool WasEntryTruncated(OutputLogEntry original, OutputLogEntry truncated) =>
        !string.Equals(original.JobId, truncated.JobId, StringComparison.Ordinal)
        || !string.Equals(original.ThreadId, truncated.ThreadId, StringComparison.Ordinal)
        || !string.Equals(original.TurnId, truncated.TurnId, StringComparison.Ordinal)
        || !string.Equals(original.AgentId, truncated.AgentId, StringComparison.Ordinal)
        || !string.Equals(original.Source, truncated.Source, StringComparison.Ordinal)
        || !string.Equals(original.Level, truncated.Level, StringComparison.Ordinal)
        || !string.Equals(original.Message, truncated.Message, StringComparison.Ordinal)
        || !string.Equals(original.PayloadJson, truncated.PayloadJson, StringComparison.Ordinal);

    private static string RenderText(IReadOnlyList<OutputLogEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(entry.CreatedAt.ToString("O"));
            builder.Append(' ');
            builder.Append(entry.Source);
            builder.Append('/');
            builder.Append(entry.Level);
            builder.Append(": ");
            builder.Append(entry.Message);
            if (!string.IsNullOrWhiteSpace(entry.PayloadJson))
            {
                builder.AppendLine();
                builder.Append(entry.PayloadJson);
            }
        }

        return builder.ToString();
    }

    private static string RenderJsonLines(IReadOnlyList<OutputLogEntry> entries) =>
        string.Join(Environment.NewLine, entries.Select(StorageJson.SerializeLine));
}
