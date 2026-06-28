namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Compatibility name for modules built before the Core/Runtime split.
/// New pipeline-only modules should implement <see cref="ISharpClawCoreModule"/>.
/// New application runtime modules should implement
/// <see cref="ISharpClawRuntimeModule"/>.
/// </summary>
[Obsolete("Use ISharpClawCoreModule for pipeline-only modules or ISharpClawRuntimeModule for application runtime modules.")]
public interface ISharpClawModule : ISharpClawRuntimeModule
{
}
