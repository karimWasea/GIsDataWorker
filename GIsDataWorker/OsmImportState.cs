namespace GIsDataWorker;

/// <summary>
/// Shared singleton signal — MapUpdateWorker sets it ready,
/// Worker waits for it before querying OSM tables.
/// </summary>
public class OsmImportState
{
    private readonly TaskCompletionSource _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Awaited by Worker before starting queries.</summary>
    public Task OsmDataReady => _tcs.Task;

    /// <summary>Called by MapUpdateWorker once OSM data is confirmed.</summary>
    public void SetReady() => _tcs.TrySetResult();

    public bool IsReady => _tcs.Task.IsCompleted;
}