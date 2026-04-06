namespace Hone.Core.Contracts;

/// <summary>
/// Generic AI agent invocation abstraction.
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Invokes an AI agent with the specified parameters and returns the result.
    /// </summary>
    public Task<AgentRunResult> InvokeAsync(AgentInvocation invocation, CancellationToken ct = default);
}
