# SharpClaw.Contracts

Contracts and interfaces for building SharpClaw modules.

## Module Types

Pipeline-only modules implement `ISharpClawCoreModule`. These modules can add
providers, tools, transcription capabilities, task parser hooks, resource and
permission descriptors, storage contracts, and other pure pipeline behavior.
They cannot publish CLI commands, API endpoints, gateway routes, or frontend
contributions because those members are not part of the core module contract.

Application runtime modules implement `ISharpClawRuntimeModule`. A runtime module
can do everything a core module can do, and can also publish the application
surfaces used by the SharpClaw API, CLI, gateway, and frontend clients.

The older `ISharpClawModule` name remains only as a compatibility name for
runtime modules. New code should choose the narrower interface that matches what
the module is allowed to contribute.

## Quick start

```bash
dotnet add package SharpClaw.Contracts
```
