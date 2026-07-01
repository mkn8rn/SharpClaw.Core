using System.Collections.Concurrent;
using System.Threading.Channels;
using SharpClaw.Contracts.DTOs.Tasks;

namespace SharpClaw.Core.Tasks.Runtime;

/// <summary>
/// Host-neutral registry for active task runtime entries.
/// </summary>
public sealed class TaskRuntimeRegistry(TimeProvider? timeProvider = null)
{
    private readonly ConcurrentDictionary<Guid, TaskRuntimeEntry> _entries = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Number of active runtime entries.
    /// </summary>
    public int ActiveCount => _entries.Count;

    /// <summary>
    /// Snapshot of active task instance identifiers.
    /// </summary>
    public IReadOnlyCollection<Guid> ActiveInstanceIds => _entries.Keys.ToArray();

    /// <summary>
    /// Registers a runtime entry and returns the task execution handle.
    /// </summary>
    public TaskRuntimeInstance Register(
        Guid instanceId,
        CancellationToken linkedToken = default)
    {
        var entry = TaskRuntimeEntry.Create(linkedToken, _timeProvider);
        _entries.AddOrUpdate(
            instanceId,
            entry,
            (_, existing) =>
            {
                existing.CompleteOutput();
                existing.Dispose();
                return entry;
            });

        return entry.CreateInstance(instanceId);
    }

    /// <summary>
    /// Removes an entry and completes its output stream.
    /// </summary>
    public bool Unregister(Guid instanceId, Exception? error = null)
    {
        if (!_entries.TryRemove(instanceId, out var entry))
            return false;

        entry.CompleteOutput(error);
        entry.Dispose();
        return true;
    }

    /// <summary>
    /// Returns whether an active runtime entry exists.
    /// </summary>
    public bool IsRunning(Guid instanceId) => _entries.ContainsKey(instanceId);

    /// <summary>
    /// Gets an active output stream reader, or <see langword="null"/>.
    /// </summary>
    public ChannelReader<TaskOutputEvent>? GetOutputReader(Guid instanceId)
        => _entries.TryGetValue(instanceId, out var entry)
            ? entry.OutputReader
            : null;

    /// <summary>
    /// Attempts to get the active entry for host-owned side effects.
    /// </summary>
    public bool TryGetEntry(Guid instanceId, out TaskRuntimeEntry entry)
        => _entries.TryGetValue(instanceId, out entry!);

    /// <summary>
    /// Pauses an active entry.
    /// </summary>
    public bool TryPause(Guid instanceId)
    {
        if (!_entries.TryGetValue(instanceId, out var entry))
            return false;

        entry.Pause();
        return true;
    }

    /// <summary>
    /// Resumes an active entry.
    /// </summary>
    public bool TryResume(Guid instanceId)
    {
        if (!_entries.TryGetValue(instanceId, out var entry))
            return false;

        entry.Resume();
        return true;
    }

    /// <summary>
    /// Resumes and cancels an active entry.
    /// </summary>
    public async Task<bool> CancelAsync(Guid instanceId)
    {
        if (!_entries.TryGetValue(instanceId, out var entry))
            return false;

        entry.Resume();
        await entry.CancelAsync();
        return true;
    }

    /// <summary>
    /// Resumes and cancels every entry active at call time.
    /// </summary>
    public async Task<int> CancelAllAsync()
    {
        var cancelled = 0;
        foreach (var id in ActiveInstanceIds)
        {
            if (await CancelAsync(id))
                cancelled++;
        }

        return cancelled;
    }
}
