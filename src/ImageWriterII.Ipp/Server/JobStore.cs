using System.Collections.Concurrent;

namespace ImageWriterII.Ipp.Server;

/// <summary>In-memory job table with a bounded history of finished jobs.</summary>
public sealed class JobStore
{
    private readonly ConcurrentDictionary<int, PrintJob> _jobs = new();
    private readonly int _keepCompleted;
    private int _nextId;

    public JobStore(int keepCompleted) => _keepCompleted = Math.Max(1, keepCompleted);

    public int NextId() => Interlocked.Increment(ref _nextId);

    public void Add(PrintJob job)
    {
        _jobs[job.Id] = job;
        Trim();
    }

    public PrintJob? Get(int id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public IEnumerable<PrintJob> All() => _jobs.Values.OrderBy(j => j.Id);

    public IEnumerable<PrintJob> NotCompleted() => All().Where(j => !j.IsFinished);

    public IEnumerable<PrintJob> Completed() => All().Where(j => j.IsFinished).OrderByDescending(j => j.Id);

    public int QueuedCount => _jobs.Values.Count(j => !j.IsFinished);

    private void Trim()
    {
        var finished = _jobs.Values.Where(j => j.IsFinished).OrderByDescending(j => j.Id).Skip(_keepCompleted).ToList();
        foreach (var j in finished)
        {
            _jobs.TryRemove(j.Id, out _);
            if (j.SpoolPath is not null) { try { File.Delete(j.SpoolPath); } catch (Exception) { /* ignore */ } }
        }
    }
}
