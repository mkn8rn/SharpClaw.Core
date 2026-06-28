# SharpClaw.Core

SharpClaw.Core is the host-agnostic business pipeline package for SharpClaw.
It is intended to contain the canonical SharpClaw state machines, capability
rules, orchestration flow, and storage port contracts without bringing along
the SharpClaw application server.

This repository is intentionally not a runnable SharpClaw application. It does
not provide an API server, CLI, database, migrations, sidecar launcher, default
module bundle, or UI. A host application embeds SharpClaw.Core and supplies
stores, module invokers, provider clients, clocks, metrics, event sinks, and
configuration.

The repository publishes two packages. `SharpClaw.Contracts` is the MIT
licensed module and provider contract package. `SharpClaw.Core` remains
AGPL-3.0 and is the host-agnostic behavior package. Core consumes Contracts
through its NuGet package dependency, not through a project reference, so the
package boundary is the same during local development and after publishing.

Contracts distinguishes pipeline-only modules from application runtime modules.
An `ISharpClawCoreModule` can add providers, tools, transcription, task parser
hooks, resources, permissions, storage contracts, and other pure pipeline
behavior. An `ISharpClawRuntimeModule` extends that same core module contract
and can also publish CLI commands, API endpoints, gateway routes, and frontend
contributions for a SharpClaw application runtime. The runtime module contract
is a superset; runtime modules can make core pipeline additions, but core
modules cannot publish application surfaces.

The first Core package shape is deliberately small. The solution contains one
packable Core class library directly at `SharpClaw.Core.csproj` and one
Contracts package at `SharpClaw.Contracts/SharpClaw.Contracts.csproj`. Core can
grow by moving host-independent pipeline behavior out of the SharpClaw
application only after the relevant storage and module boundaries are explicit.

The solution can be built from this repository root.

```powershell
dotnet build SharpClaw.Core.slnx
```
