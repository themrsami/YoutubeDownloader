using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PremiumYoutubeDownloader.Services;

/// <summary>
/// Limits how many downloads run at once. Unlike a SemaphoreSlim, the limit can change while
/// items are waiting, so changing the setting never lets more downloads run than configured.
/// </summary>
public sealed class DownloadSlotLimiter
{
    private readonly object _gate = new();
    private readonly LinkedList<TaskCompletionSource<IDisposable>> _waiters = new();
    private int _limit;
    private int _active;

    public DownloadSlotLimiter(int limit)
    {
        _limit = Math.Max(1, limit);
    }

    public int Limit
    {
        get
        {
            lock (_gate)
            {
                return _limit;
            }
        }
    }

    public void SetLimit(int limit)
    {
        List<TaskCompletionSource<IDisposable>> granted;
        lock (_gate)
        {
            _limit = Math.Max(1, limit);
            granted = GrantAvailableSlots();
        }

        Complete(granted);
    }

    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource<IDisposable> waiter;
        LinkedListNode<TaskCompletionSource<IDisposable>> node;

        lock (_gate)
        {
            if (_active < _limit && _waiters.Count == 0)
            {
                _active++;
                return Task.FromResult<IDisposable>(new Slot(this));
            }

            waiter = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() =>
            {
                bool removed;
                lock (_gate)
                {
                    removed = node.List != null;
                    if (removed)
                    {
                        _waiters.Remove(node);
                    }
                }

                if (removed)
                {
                    waiter.TrySetCanceled(cancellationToken);
                }
            });

            waiter.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }

        return waiter.Task;
    }

    private void Release()
    {
        List<TaskCompletionSource<IDisposable>> granted;
        lock (_gate)
        {
            _active = Math.Max(0, _active - 1);
            granted = GrantAvailableSlots();
        }

        Complete(granted);
    }

    private List<TaskCompletionSource<IDisposable>> GrantAvailableSlots()
    {
        var granted = new List<TaskCompletionSource<IDisposable>>();
        while (_active < _limit && _waiters.First is { } first)
        {
            _waiters.RemoveFirst();
            _active++;
            granted.Add(first.Value);
        }

        return granted;
    }

    private void Complete(List<TaskCompletionSource<IDisposable>> granted)
    {
        foreach (var waiter in granted)
        {
            if (!waiter.TrySetResult(new Slot(this)))
            {
                Release();
            }
        }
    }

    private sealed class Slot : IDisposable
    {
        private DownloadSlotLimiter? _owner;

        public Slot(DownloadSlotLimiter owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
