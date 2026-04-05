using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Queue;

namespace Hone.Orchestration.Failure;

/// <summary>
/// Unified handler for experiment failures in stacked-diffs mode.
/// Performs the standard rejection sequence: revert code, optionally record
/// metadata via a caller-supplied callback, and optionally mark the queue
/// item done.
/// </summary>
/// <remarks>
/// Mirrors <c>harness/Invoke-FailureHandler.ps1</c>.  Metadata recording is
/// delegated to the caller (the loop runner owns <c>run-metadata.json</c>)
/// through an optional <see cref="Func{T, TResult}"/> callback.
/// </remarks>
internal sealed class ExperimentFailureHandler
{
    private readonly IVersionControl _versionControl;
    private readonly OptimizationQueueManager _queueManager;
    private readonly IHoneEventSink _eventSink;

    internal ExperimentFailureHandler(
        IVersionControl versionControl,
        OptimizationQueueManager queueManager,
        IHoneEventSink eventSink)
    {
        _versionControl = versionControl;
        _queueManager = queueManager;
        _eventSink = eventSink;
    }

    /// <summary>
    /// Executes the failure-handling sequence: revert → metadata → queue mark.
    /// </summary>
    /// <param name="context">Details about the failed experiment.</param>
    /// <param name="onMetadataUpdate">
    /// Optional callback invoked when metadata should be recorded.
    /// The loop runner typically supplies this to write to <c>run-metadata.json</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result summarising which steps succeeded.</returns>
    internal async Task<FailureHandlerResult> HandleFailureAsync(
        FailureContext context,
        Func<FailureContext, Task>? onMetadataUpdate = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        bool revertSucceeded = await RevertCodeAsync(context, ct).ConfigureAwait(false);
        bool metadataUpdated = await UpdateMetadataAsync(context, onMetadataUpdate).ConfigureAwait(false);
        bool queueMarked = MarkQueueDone(context);

        _eventSink.Emit(new StatusMessage(
            $"Failure handler completed for experiment {context.Experiment}: " +
            $"revert={revertSucceeded}, metadata={metadataUpdated}, queue={queueMarked}",
            LogLevel.Info,
            DateTimeOffset.UtcNow,
            context.Experiment));

        return new FailureHandlerResult(
            Success: revertSucceeded,
            RevertSucceeded: revertSucceeded,
            MetadataUpdated: metadataUpdated,
            QueueMarked: queueMarked);
    }

    // ── Step 1: Revert ──────────────────────────────────────────────────────

    private async Task<bool> RevertCodeAsync(FailureContext context, CancellationToken ct)
    {
        try
        {
            await _versionControl.RevertLastCommitAsync(context.TargetDir, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _eventSink.Emit(new StatusMessage(
                $"Revert failed for experiment {context.Experiment}: {ex.Message}",
                LogLevel.Warning,
                DateTimeOffset.UtcNow,
                context.Experiment));
            return false;
        }
    }

    // ── Step 2: Metadata ────────────────────────────────────────────────────

    private static async Task<bool> UpdateMetadataAsync(
        FailureContext context,
        Func<FailureContext, Task>? onMetadataUpdate)
    {
        if (context.SkipMetadataUpdate || onMetadataUpdate is null)
        {
            return false;
        }

        await onMetadataUpdate(context).ConfigureAwait(false);
        return true;
    }

    // ── Step 3: Queue ───────────────────────────────────────────────────────

    private bool MarkQueueDone(FailureContext context)
    {
        if (context.SkipQueueMarkDone || string.IsNullOrEmpty(context.QueueItemId))
        {
            return false;
        }

        _queueManager.MarkDone(context.QueueItemId, context.Outcome, context.Experiment);
        return true;
    }
}
