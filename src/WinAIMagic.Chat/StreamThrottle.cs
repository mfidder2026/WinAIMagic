using System.Diagnostics;
using System.Text;

namespace WinAIMagic.Chat;

/// <summary>Text from a stream never reaches the screen directly: it collects here and goes out at most every 0.1 s, on
/// the owner's thread, never from inside Add. In the Mac version the text reached the screen from another thread and the
/// two waited for each other, which locked up the app (1.9.4). The producer never waits for the owner.</summary>
public sealed class StreamThrottle
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(0.1);

    private readonly object gate = new();
    private readonly StringBuilder content = new(), reasoning = new();
    private readonly Action<string, string> deliver;
    private readonly TimeSpan interval;
    private readonly SynchronizationContext? context;
    private readonly Timer timer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan last = TimeSpan.Zero;
    private bool scheduled;
    private readonly List<TaskCompletionSource> waiters = [];

    /// <param name="deliver">Gets (content, reasoning) on the context's thread, or on a pool thread without one.</param>
    /// <param name="interval">At least this long between two deliveries.</param>
    /// <param name="context">The UI's context, for example; at most one delivery is queued on it at a time.</param>
    public StreamThrottle(Action<string, string> deliver, TimeSpan? interval = null, SynchronizationContext? context = null)
    {
        this.deliver = deliver;
        this.interval = interval ?? DefaultInterval;
        this.context = context;
        timer = new Timer(_ => Due());
    }

    /// <summary>From any thread; returns at once.</summary>
    public void Add(string text, string thinking = "")
    {
        if (text.Length == 0 && thinking.Length == 0) return;
        lock (gate)
        {
            content.Append(text);
            reasoning.Append(thinking);
            Schedule();
        }
    }

    /// <summary>Completes when everything added so far has been delivered. Await it; do not block the owner on it.</summary>
    public Task DrainAsync()
    {
        lock (gate)
        {
            if (!scheduled && content.Length == 0 && reasoning.Length == 0) return Task.CompletedTask;
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add(waiter);
            return waiter.Task;
        }
    }

    private void Schedule()
    {
        if (scheduled) return;
        scheduled = true;
        var wait = last + interval - clock.Elapsed;
        var least = TimeSpan.FromMilliseconds(1);
        timer.Change(wait > least ? wait : least, Timeout.InfiniteTimeSpan);
    }

    private void Due()
    {
        if (context != null) context.Post(_ => Deliver(), null);
        else Deliver();
    }

    private void Deliver()
    {
        string c, r;
        lock (gate)
        {
            c = content.ToString();
            r = reasoning.ToString();
            content.Clear();
            reasoning.Clear();
        }
        try
        {
            if (c.Length > 0 || r.Length > 0) deliver(c, r);
        }
        finally
        {
            List<TaskCompletionSource>? done = null;
            lock (gate)
            {
                last = clock.Elapsed;
                scheduled = false;
                if (content.Length > 0 || reasoning.Length > 0) Schedule();
                else { done = [.. waiters]; waiters.Clear(); }
            }
            done?.ForEach(w => w.TrySetResult());
        }
    }
}
