namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Contract for a SharpClaw Runtime module. Runtime modules can do everything
/// a Core module can do, and can also publish application runtime surfaces
/// consumed by the API host, CLI, gateway, and frontend clients.
/// </summary>
public interface ISharpClawRuntimeModule : ISharpClawCoreModule
{
    /// <summary>
    /// Optional. Return UI contribution descriptors for this module.
    /// </summary>
    IReadOnlyList<ModuleUiContribution> GetUiContributions() => [];

    /// <summary>
    /// Optional. Return typed frontend contribution descriptors for this
    /// module.
    /// </summary>
    IReadOnlyList<ModuleFrontendContribution> GetFrontendContributions() => [];

    /// <summary>
    /// Optional. Return CLI commands this module provides.
    /// </summary>
    IReadOnlyList<ModuleCliCommand>? GetCliCommands() => null;

    /// <summary>
    /// Optional. Map HTTP endpoints owned by this module.
    /// </summary>
    /// <param name="app">An application endpoint builder supplied by the runtime host.</param>
    void MapEndpoints(object app) { }
}
