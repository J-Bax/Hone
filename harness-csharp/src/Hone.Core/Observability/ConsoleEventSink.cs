namespace Hone.Core.Observability;

/// <summary>
/// Writes harness events to a <see cref="TextWriter"/> with timestamped formatting.
/// Replaces PowerShell <c>Write-Status</c>.
/// </summary>
/// <remarks>
/// Box-drawing characters (━═─╔╚╗╝║╠╣╦╩) at the start of a message are passed
/// through without a timestamp prefix, preserving visual formatting parity with
/// the PowerShell baseline.
/// </remarks>
/// <param name="writer">Destination for formatted event output.</param>
public sealed class ConsoleEventSink(TextWriter writer) : IHoneEventSink
{
    private const string BoxDrawingChars = "━═─╔╚╗╝║╠╣╦╩";

    private readonly TextWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    /// <inheritdoc/>
    public void Emit(HoneEvent honeEvent)
    {
        ArgumentNullException.ThrowIfNull(honeEvent);

        string line = honeEvent switch
        {
            StatusMessage sm => FormatStatusMessage(sm),
            PhaseStarted ps => FormatTimestamped(ps.Timestamp, $"Phase '{ps.Phase}' started (experiment {ps.Experiment})"),
            PhaseCompleted pc => FormatTimestamped(pc.Timestamp, $"Phase '{pc.Phase}' {(pc.Success ? "completed" : "failed")} in {pc.Duration.TotalSeconds:F1}s"),
            AgentInvoked ai => FormatTimestamped(ai.Timestamp, $"Agent '{ai.AgentName}' ({ai.Model}) {(ai.Success ? "succeeded" : "failed")} in {ai.Duration.TotalSeconds:F1}s"),
            ExperimentOutcomeEvent eo => FormatTimestamped(eo.Timestamp, $"Experiment {eo.Experiment} outcome: {eo.Outcome}"),
            MetricSnapshot ms => FormatTimestamped(ms.Timestamp, $"Metrics snapshot for experiment {ms.Experiment}"),
            DiagnosticProgress dp => FormatTimestamped(dp.Timestamp, $"Diagnostic '{dp.CollectorName}': {dp.Stage}"),
            _ => FormatTimestamped(honeEvent.Timestamp, honeEvent.ToString()),
        };

        _writer.WriteLine(line);
    }

    private static string FormatStatusMessage(StatusMessage sm)
    {
        string message = sm.Message;

        if (string.IsNullOrWhiteSpace(message) || StartsWithBoxDrawing(message))
        {
            return message;
        }

        return $"[{sm.Timestamp:HH:mm:ss}] {message}";
    }

    private static string FormatTimestamped(DateTimeOffset timestamp, string text)
        => $"[{timestamp:HH:mm:ss}] {text}";

    private static bool StartsWithBoxDrawing(string message)
        => message.Length > 0 && BoxDrawingChars.Contains(message[0], StringComparison.Ordinal);
}
