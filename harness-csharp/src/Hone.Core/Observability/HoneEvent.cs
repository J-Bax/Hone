using System.Text.Json.Serialization;
using Hone.Core.Models;

namespace Hone.Core.Observability;

/// <summary>
/// Base record for all structured events emitted by the Hone harness.
/// </summary>
[JsonDerivedType(typeof(PhaseStarted), "PhaseStarted")]
[JsonDerivedType(typeof(PhaseCompleted), "PhaseCompleted")]
[JsonDerivedType(typeof(MetricSnapshot), "MetricSnapshot")]
[JsonDerivedType(typeof(AgentInvoked), "AgentInvoked")]
[JsonDerivedType(typeof(ExperimentOutcomeEvent), "ExperimentOutcomeEvent")]
[JsonDerivedType(typeof(StatusMessage), "StatusMessage")]
[JsonDerivedType(typeof(DiagnosticProgress), "DiagnosticProgress")]
public abstract record HoneEvent(DateTimeOffset Timestamp, int? Experiment);

/// <summary>
/// Emitted when a harness phase begins execution.
/// </summary>
public sealed record PhaseStarted(
    string Phase,
    DateTimeOffset Timestamp,
    int? Experiment) : HoneEvent(Timestamp, Experiment);

/// <summary>
/// Emitted when a harness phase completes execution.
/// </summary>
public sealed record PhaseCompleted(
    string Phase,
    TimeSpan Duration,
    bool Success,
    DateTimeOffset Timestamp,
    int? Experiment) : HoneEvent(Timestamp, Experiment);

/// <summary>
/// Snapshot of metrics collected during an experiment run.
/// </summary>
public sealed record MetricSnapshot(
    MetricSet Metrics,
    DateTimeOffset Timestamp,
    int? Experiment) : HoneEvent(Timestamp, Experiment);

/// <summary>
/// Records an AI agent invocation and its outcome.
/// </summary>
public sealed record AgentInvoked(
    string AgentName,
    string Model,
    TimeSpan Duration,
    bool Success,
    DateTimeOffset Timestamp,
    int? Experiment = null) : HoneEvent(Timestamp, Experiment);

/// <summary>
/// Records the final outcome of an experiment.
/// </summary>
public sealed record ExperimentOutcomeEvent(
    string Outcome,
    ComparisonResult Details,
    DateTimeOffset Timestamp,
    int? Experiment) : HoneEvent(Timestamp, Experiment);

/// <summary>
/// A human-readable status message with severity level.
/// Replaces PowerShell <c>Write-Status</c>.
/// </summary>
public sealed record StatusMessage(
    string Message,
    LogLevel Level,
    DateTimeOffset Timestamp,
    int? Experiment = null) : HoneEvent(Timestamp, Experiment);

/// <summary>
/// Progress update from a diagnostic data collector.
/// </summary>
public sealed record DiagnosticProgress(
    string CollectorName,
    string Stage,
    DateTimeOffset Timestamp,
    int? Experiment = null) : HoneEvent(Timestamp, Experiment);
