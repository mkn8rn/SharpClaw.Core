using System.Text.Json;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Core.Modules.Foreign;

public sealed record ForeignModuleTaskStepExecutionRequest(
    int ProtocolVersion,
    string ModuleId,
    string StepKey,
    ForeignModuleTaskStepExecutionContextSnapshot Context,
    IReadOnlyList<string>? Arguments = null,
    string? Expression = null,
    string? ResultVariable = null);

public sealed record ForeignModuleTaskStepInvocationRequest(
    int ProtocolVersion,
    string ModuleId,
    ForeignModuleTaskStepInvocationDescriptor Step,
    ForeignModuleTaskStepExecutionContextSnapshot Context);

public sealed record ForeignModuleTaskStepExecutionContextSnapshot(
    Guid InstanceId,
    Guid ChannelId,
    IReadOnlyDictionary<string, JsonElement>? Variables = null,
    IReadOnlyList<ForeignModuleTaskEventHandlerSnapshot>? EventHandlers = null,
    string? ContextCallbackId = null);

public sealed record ForeignModuleTaskEventHandlerSnapshot(
    string? ModuleTriggerKey,
    string? ParameterName,
    string? HandlerCallbackId = null);

public sealed record ForeignModuleTaskStepInvocationDescriptor(
    string StepKey,
    string? VariableName = null,
    string? TypeName = null,
    string? ResultVariable = null,
    string? RawExpression = null,
    IReadOnlyList<string>? Arguments = null,
    string? ModuleTriggerKey = null,
    string? HandlerParameter = null,
    IReadOnlyList<ForeignModuleTaskStepInvocationDescriptor>? Body = null,
    IReadOnlyList<ForeignModuleTaskStepInvocationDescriptor>? ElseBody = null)
{
    public static ForeignModuleTaskStepInvocationDescriptor From(
        ITaskStepInvocation step) =>
        new(
            step.StepKey,
            step.VariableName,
            step.TypeName,
            step.ResultVariable,
            step.RawExpression,
            step.Arguments,
            step.ModuleTriggerKey,
            step.HandlerParameter,
            step.Body is null ? null : [.. step.Body.Select(From)],
            step.ElseBody is null ? null : [.. step.ElseBody.Select(From)]);
}

public sealed record ForeignModuleTaskStepExecutionResponse(
    TaskStepResult Result = TaskStepResult.Continue,
    bool? Continue = null,
    IReadOnlyDictionary<string, JsonElement>? VariableUpdates = null,
    JsonElement? ResultVariableValue = null,
    IReadOnlyList<string>? Logs = null,
    string? OutputJson = null,
    Guid? ChannelId = null,
    IReadOnlyList<ForeignModuleTaskRegisteredEventHandlerDescriptor>? RegisteredEventHandlers = null);

public sealed record ForeignModuleTaskRegisteredEventHandlerDescriptor(
    string ModuleTriggerKey,
    string? ParameterName,
    IReadOnlyList<ForeignModuleTaskStepInvocationDescriptor> Body);
