using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hone.Core.Models;
using Hone.Core.Observability;

namespace Hone.Orchestration.Queue;

/// <summary>
/// Manages the structured optimization queue (experiment-queue.json) that stores
/// ranked optimization opportunities from the analysis agent. The queue drives
/// the experiment loop.
/// </summary>
internal sealed class OptimizationQueueManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        WriteIndented = true,
    };

    private readonly string _metadataDir;
    private readonly IHoneEventSink _eventSink;
    private readonly string _queueJsonPath;
    private readonly string _queueMdPath;
    private readonly Lock _lock = new();

    internal OptimizationQueueManager(string metadataDir, IHoneEventSink eventSink)
    {
        _metadataDir = metadataDir;
        _eventSink = eventSink;
        _queueJsonPath = Path.Combine(metadataDir, "experiment-queue.json");
        _queueMdPath = Path.Combine(metadataDir, "experiment-queue.md");
    }

    /// <summary>
    /// Creates or replaces the queue from analysis output.
    /// </summary>
    internal InitializeResult Initialize(IReadOnlyList<Opportunity> opportunities, int experiment)
    {
        if (opportunities is null or { Count: 0 })
        {
            return new InitializeResult(Success: false, Count: 0);
        }

        Directory.CreateDirectory(_metadataDir);

        var items = new List<QueueItemDto>(opportunities.Count);

        for (int i = 0; i < opportunities.Count; i++)
        {
            Opportunity opp = opportunities[i];
            string id = (i + 1).ToString(CultureInfo.InvariantCulture);

            string title = !string.IsNullOrEmpty(opp.Title) ? opp.Title
                         : !string.IsNullOrEmpty(opp.Explanation) ? opp.Explanation
                         : string.Empty;

            string explanation = !string.IsNullOrEmpty(opp.Explanation) ? opp.Explanation : title;

            string? rcaPath = null;
            if (!string.IsNullOrEmpty(opp.RootCause))
            {
                string rcaDir = Path.Combine(_metadataDir, "root-causes");
                Directory.CreateDirectory(rcaDir);
                string rcaFile = Path.Combine(rcaDir, $"rca-{id}.md");
                string scopeStr = opp.Scope switch
                {
                    OpportunityScope.Narrow => "narrow",
                    OpportunityScope.Architecture => "architecture",
                    OpportunityScope.Unknown or _ => "narrow",
                };
                string rcaContent = $"# {title}\n\n> **File:** `{opp.FilePath}` | **Scope:** {scopeStr}\n\n{opp.RootCause}";
                File.WriteAllText(rcaFile, rcaContent, Encoding.UTF8);
                rcaPath = rcaFile;
            }

            items.Add(new QueueItemDto
            {
                Id = id,
                FilePath = opp.FilePath,
                Title = title,
                Explanation = explanation,
                Scope = opp.Scope,
                RootCausePath = rcaPath,
                Status = QueueItemStatus.Pending,
                TriedByExperiment = null,
                Outcome = null,
            });
        }

        var queue = new QueueFileDto
        {
            GeneratedByExperiment = experiment,
            GeneratedAt = DateTimeOffset.UtcNow,
            Items = items,
        };

        lock (_lock)
        {
            AtomicWriteJson(queue);
            WriteMarkdown(queue);
        }

        _eventSink.Emit(new StatusMessage(
            $"Initialized optimization queue with {items.Count} items",
            LogLevel.Info,
            DateTimeOffset.UtcNow,
            experiment));

        return new InitializeResult(Success: true, Count: items.Count);
    }

    /// <summary>
    /// Returns the next pending actionable (non-architecture) item and marks it in-progress.
    /// </summary>
    internal QueueItem? GetNext(int experiment)
    {
        QueueItem? result;

        lock (_lock)
        {
            QueueFileDto queue = ReadQueue();

            int nextIdx = -1;
            for (int i = 0; i < queue.Items.Count; i++)
            {
                if (queue.Items[i].Status == QueueItemStatus.Pending &&
                    queue.Items[i].Scope != OpportunityScope.Architecture)
                {
                    nextIdx = i;
                    break;
                }
            }

            if (nextIdx < 0)
            {
                return null;
            }

            QueueItemDto original = queue.Items[nextIdx];
            queue.Items[nextIdx] = original with { Status = QueueItemStatus.InProgress };

            AtomicWriteJson(queue);
            WriteMarkdown(queue);

            result = ToQueueItem(queue.Items[nextIdx]);
        }

        _eventSink.Emit(new StatusMessage(
            $"Picked queue item #{result.Id}: {result.FilePath}",
            LogLevel.Info,
            DateTimeOffset.UtcNow,
            experiment));

        return result;
    }

    /// <summary>
    /// Returns the root-cause document content for the given queue item, or <c>null</c>
    /// if no RCA was generated.
    /// </summary>
    internal string? GetRootCauseDocument(string itemId)
    {
        lock (_lock)
        {
            QueueFileDto queue = ReadQueue();
            QueueItemDto? dto = queue.Items.Find(i => string.Equals(i.Id, itemId, StringComparison.Ordinal));
            if (dto?.RootCausePath is not null && File.Exists(dto.RootCausePath))
            {
                return File.ReadAllText(dto.RootCausePath, Encoding.UTF8);
            }

            return null;
        }
    }

    /// <summary>
    /// Returns true if any pending non-architecture items exist.
    /// </summary>
    internal bool HasActionable()
    {
        lock (_lock)
        {
            QueueFileDto queue = ReadQueue();
            return queue.Items.Exists(i =>
                i.Status == QueueItemStatus.Pending &&
                i.Scope != OpportunityScope.Architecture);
        }
    }

    /// <summary>
    /// Marks an item as done with the given outcome and experiment number.
    /// </summary>
    internal void MarkDone(string itemId, string outcome, int experiment)
    {
        lock (_lock)
        {
            QueueFileDto queue = ReadQueue();

            for (int i = 0; i < queue.Items.Count; i++)
            {
                if (string.Equals(queue.Items[i].Id, itemId, StringComparison.Ordinal))
                {
                    queue.Items[i] = queue.Items[i] with
                    {
                        Status = QueueItemStatus.Done,
                        TriedByExperiment = experiment,
                        Outcome = outcome,
                    };
                    break;
                }
            }

            AtomicWriteJson(queue);
            WriteMarkdown(queue);
        }

        _eventSink.Emit(new StatusMessage(
            $"Queue item #{itemId} marked done: {outcome}",
            LogLevel.Info,
            DateTimeOffset.UtcNow,
            experiment));
    }

    /// <summary>
    /// Regenerates experiment-queue.md from the JSON source.
    /// </summary>
    internal void SyncMarkdown()
    {
        lock (_lock)
        {
            QueueFileDto queue = ReadQueue();
            WriteMarkdown(queue);
        }
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private QueueFileDto ReadQueue()
    {
        if (!File.Exists(_queueJsonPath))
        {
            return new QueueFileDto();
        }

        string json = File.ReadAllText(_queueJsonPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<QueueFileDto>(json, SerializerOptions)
            ?? new QueueFileDto();
    }

    private void AtomicWriteJson(QueueFileDto queue)
    {
        string json = JsonSerializer.Serialize(queue, SerializerOptions);
        string tmpPath = _queueJsonPath + ".tmp";
        File.WriteAllText(tmpPath, json, Encoding.UTF8);
        File.Move(tmpPath, _queueJsonPath, overwrite: true);
    }

    private void WriteMarkdown(QueueFileDto queue)
    {
        var sb = new StringBuilder();
        sb.Append("# Optimization Queue\n");
        sb.Append('\n');
        sb.Append("> Auto-generated from experiment-queue.json by Hone.\n");
        sb.Append("> Do not edit this file directly \u2014 it is regenerated after each queue mutation.\n");
        sb.Append('\n');

        if (queue.GeneratedAt != default)
        {
            sb.Append(CultureInfo.InvariantCulture, $"**Generated:** experiment {queue.GeneratedByExperiment} at {queue.GeneratedAt:o}\n");
            sb.Append('\n');
        }

        foreach (QueueItemDto item in queue.Items)
        {
            string check = item.Status is QueueItemStatus.Pending or QueueItemStatus.InProgress ? " " : "x";
            string scopeTag = item.Scope == OpportunityScope.Architecture ? "[ARCHITECTURE] " : "";
            string statusNote = item.Status switch
            {
                QueueItemStatus.Pending => "",
                QueueItemStatus.InProgress => " *(in progress)*",
                QueueItemStatus.Done => $" *(experiment {item.TriedByExperiment} \u2014 {item.Outcome})*",
                QueueItemStatus.Skipped => "",
                QueueItemStatus.Unknown or _ => "",
            };

            sb.Append(CultureInfo.InvariantCulture, $"- [{check}] **#{item.Id}**{scopeTag}`{item.FilePath}` \u2014 {item.Title}{statusNote}\n");
        }

        File.WriteAllText(_queueMdPath, sb.ToString(), Encoding.UTF8);
    }

    private static QueueItem ToQueueItem(QueueItemDto dto) =>
        new(dto.Id, dto.FilePath, dto.Explanation, dto.Scope, dto.Status, dto.TriedByExperiment, dto.Outcome);

    // ── JSON serialization DTOs ─────────────────────────────────────────────

    /// <summary>JSON shape for experiment-queue.json (includes generatedAt and title).</summary>
    private sealed record QueueFileDto
    {
        public int GeneratedByExperiment { get; init; }
        public DateTimeOffset GeneratedAt { get; init; }
        public List<QueueItemDto> Items { get; init; } = [];
    }

    /// <summary>JSON shape for a single queue item (includes title and rootCausePath).</summary>
    private sealed record QueueItemDto
    {
        public string Id { get; init; } = "";
        public string FilePath { get; init; } = "";
        public string Title { get; init; } = "";
        public string Explanation { get; init; } = "";

        public OpportunityScope Scope { get; init; }
        public string? RootCausePath { get; init; }
        public QueueItemStatus Status { get; init; }

        public int? TriedByExperiment { get; init; }
        public string? Outcome { get; init; }
    }
}
