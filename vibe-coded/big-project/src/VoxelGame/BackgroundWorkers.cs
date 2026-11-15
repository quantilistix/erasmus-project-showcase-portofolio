using System.Threading.Channels;

namespace VoxelGame;


/// <summary>
/// Runs one kind of job on background threads: jobs are posted in and results taken out as
/// they finish, in no particular order. Nothing is shared but the jobs and results, which
/// cross as messages, so the work must only read what its job carries.
/// </summary>
internal sealed class BackgroundWorkers<TJob, TResult> : IDisposable
{
    public BackgroundWorkers(string name, int threads, Func<TJob, TResult> work)
    {
        _work = work;
        _threads = [.. Enumerable.Range(0, threads).Select(i => new Thread(Run) { IsBackground = true, Name = $"{name} {i}", Priority = ThreadPriority.BelowNormal })];
        foreach (Thread thread in _threads)
            thread.Start();
    }

    private readonly Func<TJob, TResult> _work;
    private readonly Channel<TJob> _inbox = Channel.CreateUnbounded<TJob>();
    private readonly Channel<TResult> _outbox = Channel.CreateUnbounded<TResult>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Thread[] _threads;
    private volatile bool _stopping;

    /// <summary>Jobs posted whose results have not been taken yet.</summary>
    public int Pending { get; private set; }

    public void Post(TJob job)
    {
        Pending++;
        _inbox.Writer.TryWrite(job);
    }

    public bool TryTake(out TResult result)
    {
        if (!_outbox.Reader.TryRead(out result!))
            return false;

        Pending--;
        return true;
    }

    private void Run()
    {
        while (!_stopping && _inbox.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (!_stopping && _inbox.Reader.TryRead(out TJob? job))
                _outbox.Writer.TryWrite(_work(job));
        }
    }

    /// <summary>Stops the threads, leaving the jobs still waiting undone.</summary>
    public void Dispose()
    {
        _stopping = true;
        _inbox.Writer.TryComplete();
        foreach (Thread thread in _threads)
            thread.Join();
    }
}
