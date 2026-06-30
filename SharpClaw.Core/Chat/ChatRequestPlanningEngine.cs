using System.Text.Json;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Clients;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Plans the provider-facing shape of a chat request from host-loaded facts.
/// </summary>
public sealed class ChatRequestPlanningEngine(
    ChatPromptEngine prompts,
    ProviderApiClientFactory clientFactory)
{
    /// <summary>
    /// Builds the provider-call plan for a non-streaming chat request.
    /// </summary>
    public ChatRequestPlan BuildBufferedPlan(
        ChannelDB channel,
        AgentDB agent,
        Guid? threadId,
        bool disableDefaultSystemPrompt,
        bool disableCustomProviderParameters)
    {
        var facts = ResolveFacts(agent);
        var requiresApiKey = ResolveRequiresApiKey(facts.Provider);
        EnsureApiKeyConfigured(facts.Provider, requiresApiKey);
        var client = clientFactory.GetClient(
            facts.Provider.ProviderKey,
            facts.Provider.ApiEndpoint);

        var disableTools = channel.DisableToolSchemas || agent.DisableToolSchemas;
        var useNativeTools = client.SupportsNativeToolCalling;
        var enableTools = !disableTools && useNativeTools;
        var completionParameters = BuildAndValidateCompletionParameters(
            agent,
            facts.Model,
            facts.Provider,
            threadId);

        return new ChatRequestPlan(
            Client: client,
            RequiresApiKey: requiresApiKey,
            UseNativeTools: useNativeTools,
            DisableTools: disableTools,
            EnableTools: enableTools,
            SupportsVision: facts.Model.CapabilityTags.Contains(
                WellKnownCapabilityKeys.Vision),
            SystemPrompt: prompts.BuildEffectiveSystemPrompt(
                agent.SystemPrompt,
                enableTools,
                disableDefaultSystemPrompt),
            CompletionParameters: completionParameters,
            MaxCompletionTokens: agent.MaxCompletionTokens,
            ProviderParameters: disableCustomProviderParameters
                ? null
                : agent.ProviderParameters,
            ToolAwareness: enableTools
                ? channel.ToolAwarenessSet?.Tools ?? agent.ToolAwarenessSet?.Tools
                : null,
            ModelCapabilityTags: facts.Model.CapabilityTags,
            ModelId: facts.Model.Id,
            ModelName: facts.Model.Name,
            ProviderKey: facts.Provider.ProviderKey,
            ProviderName: facts.Provider.Name,
            ProviderEndpoint: facts.Provider.ApiEndpoint);
    }

    /// <summary>
    /// Builds the provider-call plan for a streaming chat request.
    /// </summary>
    public ChatRequestPlan BuildStreamingPlan(
        ChannelDB channel,
        AgentDB agent,
        Guid? threadId,
        bool disableDefaultSystemPrompt,
        bool disableCustomProviderParameters)
    {
        var facts = ResolveFacts(agent);
        var requiresApiKey = ResolveRequiresApiKey(facts.Provider);
        EnsureApiKeyConfigured(facts.Provider, requiresApiKey);
        var client = clientFactory.GetClient(
            facts.Provider.ProviderKey,
            facts.Provider.ApiEndpoint);

        var disableTools = channel.DisableToolSchemas || agent.DisableToolSchemas;
        var enableTools = !disableTools;
        var completionParameters = BuildAndValidateCompletionParameters(
            agent,
            facts.Model,
            facts.Provider,
            threadId);

        return new ChatRequestPlan(
            Client: client,
            RequiresApiKey: requiresApiKey,
            UseNativeTools: client.SupportsNativeToolCalling,
            DisableTools: disableTools,
            EnableTools: enableTools,
            SupportsVision: facts.Model.CapabilityTags.Contains(
                WellKnownCapabilityKeys.Vision),
            SystemPrompt: prompts.BuildEffectiveSystemPrompt(
                agent.SystemPrompt,
                enableTools,
                disableDefaultSystemPrompt),
            CompletionParameters: completionParameters,
            MaxCompletionTokens: agent.MaxCompletionTokens,
            ProviderParameters: disableCustomProviderParameters
                ? null
                : agent.ProviderParameters,
            ToolAwareness: enableTools
                ? channel.ToolAwarenessSet?.Tools ?? agent.ToolAwarenessSet?.Tools
                : null,
            ModelCapabilityTags: facts.Model.CapabilityTags,
            ModelId: facts.Model.Id,
            ModelName: facts.Model.Name,
            ProviderKey: facts.Provider.ProviderKey,
            ProviderName: facts.Provider.Name,
            ProviderEndpoint: facts.Provider.ApiEndpoint);
    }

    private CompletionParameters BuildAndValidateCompletionParameters(
        AgentDB agent,
        ModelDB model,
        ProviderDB provider,
        Guid? threadId)
    {
        var completionParameters = prompts.BuildCompletionParameters(
            agent,
            model.Id,
            threadId);
        CompletionParameterValidator.ValidateOrThrow(
            completionParameters,
            clientFactory.GetParameterSpec(provider.ProviderKey),
            provider.ProviderKey);
        return completionParameters;
    }

    private bool ResolveRequiresApiKey(ProviderDB provider)
    {
        return clientFactory.GetPlugin(provider.ProviderKey)?.RequiresApiKey ?? true;
    }

    private static void EnsureApiKeyConfigured(
        ProviderDB provider,
        bool requiresApiKey)
    {
        if (requiresApiKey && string.IsNullOrEmpty(provider.EncryptedApiKey))
            throw new InvalidOperationException(
                "Provider does not have an API key configured.");
    }

    private static ChatRequestFacts ResolveFacts(AgentDB agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var model = agent.Model
            ?? throw new InvalidOperationException(
                $"Agent '{agent.Name}' ({agent.Id}) has no model assigned. " +
                "Assign a valid model before using this agent for chat.");
        var provider = model.Provider
            ?? throw new InvalidOperationException(
                $"Model '{model.Name}' ({model.Id}) has no provider assigned.");

        return new ChatRequestFacts(model, provider);
    }

    private sealed record ChatRequestFacts(ModelDB Model, ProviderDB Provider);
}

/// <summary>
/// Provider-facing request plan produced from store-loaded chat facts.
/// </summary>
public sealed record ChatRequestPlan(
    IProviderApiClient Client,
    bool RequiresApiKey,
    bool UseNativeTools,
    bool DisableTools,
    bool EnableTools,
    bool SupportsVision,
    string SystemPrompt,
    CompletionParameters CompletionParameters,
    int? MaxCompletionTokens,
    Dictionary<string, JsonElement>? ProviderParameters,
    Dictionary<string, bool>? ToolAwareness,
    IReadOnlySet<string> ModelCapabilityTags,
    Guid ModelId,
    string ModelName,
    string ProviderKey,
    string ProviderName,
    string? ProviderEndpoint);
