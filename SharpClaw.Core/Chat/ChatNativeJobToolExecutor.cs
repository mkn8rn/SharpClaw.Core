using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Tools;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral native chat tool-call state machine for tool calls that
/// become SharpClaw agent jobs.
/// </summary>
public sealed class ChatNativeJobToolExecutor(
    ChatNativeToolCallParser toolCallParser,
    ChatToolResultEngine toolResults)
{
    /// <summary>
    /// Resolves a native provider tool call, submits the corresponding agent
    /// job through host delegates, applies the chat approval flow, and returns
    /// the provider-facing tool-result message plus persisted notation.
    /// </summary>
    public async Task<ChatNativeJobToolExecutionResult> ExecuteAsync(
        ChatNativeJobToolExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var toolCall = request.ResolutionRequest.ToolCall;
        var parsed = await toolCallParser.ResolveAsync(
            request.ResolutionRequest,
            ct);

        if (parsed is null)
        {
            return new ChatNativeJobToolExecutionResult(
                Parsed: false,
                ToolResultMessage: ToolAwareMessage.ToolResult(
                    toolCall.Id,
                    ChatNativeToolCallParser.MalformedToolCallResult),
                JobResponse: null,
                SubmittedJobId: null,
                ToolNotation: "",
                AwaitingUnresolvableApproval: false,
                StreamEvents: []);
        }

        var jobRequest = toolCallParser.BuildJobRequest(
            parsed,
            request.AgentId);
        var submittedJob = await request.SubmitJobAsync(
            request.ChannelId,
            jobRequest,
            ct);
        var jobResponse = submittedJob;
        var streamEvents = request.EmitStreamEvents
            ? new List<ChatStreamEvent>()
            : null;

        if (jobResponse.Status == AgentJobStatus.AwaitingApproval)
        {
            if (request.ApprovalCallback is not null)
            {
                var canApprove = await request.CanApproveAsync(
                    request.AgentId,
                    jobRequest.ResourceId,
                    jobRequest.ActionKey,
                    ct);

                if (canApprove)
                {
                    streamEvents?.Add(ChatStreamEvent.NeedsApproval(jobResponse));

                    var approved = await request.ApprovalCallback(
                        jobResponse,
                        ct);

                    if (approved)
                    {
                        if (request.ApproveJobAsync is null)
                            throw new InvalidOperationException(
                                "Approval handling requires an approve delegate.");

                        jobResponse = await request.ApproveJobAsync(
                            jobResponse.Id,
                            ct)
                            ?? jobResponse;
                    }
                    else
                    {
                        jobResponse = await request.CancelJobAsync(
                            jobResponse.Id,
                            ct)
                            ?? jobResponse;
                    }
                }
                else
                {
                    jobResponse = await request.CancelJobAsync(
                        jobResponse.Id,
                        ct)
                        ?? jobResponse;
                }

                streamEvents?.Add(ChatStreamEvent.ApprovalDecision(jobResponse));
            }
        }
        else
        {
            streamEvents?.Add(ChatStreamEvent.ToolStart(jobResponse));
        }

        return new ChatNativeJobToolExecutionResult(
            Parsed: true,
            ToolResultMessage: toolResults.BuildToolResultMessage(
                toolCall.Id,
                jobResponse,
                request.SupportsVision),
            JobResponse: jobResponse,
            SubmittedJobId: submittedJob.Id,
            ToolNotation: ToolNotationFormatter.ForJob(jobResponse),
            AwaitingUnresolvableApproval:
                jobResponse.Status == AgentJobStatus.AwaitingApproval,
            StreamEvents: streamEvents ?? []);
    }
}

/// <summary>
/// Inputs required by Core to run one native chat tool call through the
/// SharpClaw job pipeline while the host owns persistence and session state.
/// </summary>
public sealed record ChatNativeJobToolExecutionRequest(
    ChatNativeToolCallResolutionRequest ResolutionRequest,
    Guid AgentId,
    Guid ChannelId,
    bool SupportsVision,
    bool EmitStreamEvents,
    Func<Guid, SubmitAgentJobRequest, CancellationToken, Task<AgentJobResponse>>
        SubmitJobAsync,
    Func<Guid, Guid?, string?, CancellationToken, Task<bool>> CanApproveAsync,
    Func<Guid, CancellationToken, Task<AgentJobResponse?>> CancelJobAsync,
    Func<AgentJobResponse, CancellationToken, Task<bool>>? ApprovalCallback = null,
    Func<Guid, CancellationToken, Task<AgentJobResponse?>>? ApproveJobAsync = null);

/// <summary>
/// Result of running one native chat tool call through the SharpClaw job
/// pipeline.
/// </summary>
public sealed record ChatNativeJobToolExecutionResult(
    bool Parsed,
    ToolAwareMessage ToolResultMessage,
    AgentJobResponse? JobResponse,
    Guid? SubmittedJobId,
    string ToolNotation,
    bool AwaitingUnresolvableApproval,
    IReadOnlyList<ChatStreamEvent> StreamEvents);
