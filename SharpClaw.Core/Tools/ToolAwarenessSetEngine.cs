using SharpClaw.Contracts.DTOs.Tools;
using SharpClaw.Contracts.Entities.Core;

namespace SharpClaw.Core.Tools;

/// <summary>
/// Store-neutral tool-awareness set creation, mutation, and projection rules.
/// </summary>
public sealed class ToolAwarenessSetEngine
{
    /// <summary>Creates a tool-awareness set entity from a request.</summary>
    public ToolAwarenessSetDB Create(CreateToolAwarenessSetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ToolAwarenessSetDB
        {
            Name = request.Name,
            Tools = request.Tools ?? []
        };
    }

    /// <summary>Applies an update request to a loaded tool-awareness set.</summary>
    public void ApplyUpdate(
        ToolAwarenessSetDB entity,
        UpdateToolAwarenessSetRequest request)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Name is not null)
            entity.Name = request.Name;

        if (request.Tools is not null)
            entity.Tools = request.Tools;
    }

    /// <summary>Projects a loaded entity to its response shape.</summary>
    public ToolAwarenessSetResponse ToResponse(ToolAwarenessSetDB entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new ToolAwarenessSetResponse(
            entity.Id,
            entity.Name,
            entity.Tools,
            entity.CreatedAt,
            entity.UpdatedAt);
    }
}
