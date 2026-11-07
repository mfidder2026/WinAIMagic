namespace WinAIMagic.Chat;

/// <summary>An engine writes one answer at a time (it runs with one slot). Conversations take turns here, first come first
/// served, and only while the model writes: a conversation that waits for the user's approval, or runs a command, leaves
/// the model to the others. Each engine has its own gate, so the second engine can answer while the main one writes.</summary>
public sealed class ModelGate
{
    private readonly object sync = new();
    private readonly LinkedList<(Guid Owner, TaskCompletionSource<bool> Go)> queue = new();

    /// <summary>The conversation the model writes for now.</summary>
    public Guid? Holder { get; private set; }

    /// <summary>The conversations waiting their turn, in order.</summary>
    public IReadOnlyList<Guid> Waiting
    {
        get { lock (sync) return [.. queue.Select(q => q.Owner)]; }
    }

    /// <summary>The holder or the queue changed; on whatever thread made the change.</summary>
    public event Action? Changed;

    /// <summary>Waits for the model. True: it is yours, call Release when the answer is written. False: cancelled while waiting.</summary>
    public async Task<bool> AcquireAsync(Guid owner, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        LinkedListNode<(Guid, TaskCompletionSource<bool>)> node;
        lock (sync)
        {
            if (Holder == null)
            {
                Holder = owner;
                node = null!;
            }
            else node = queue.AddLast((owner, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)));
        }
        Changed?.Invoke();
        if (node == null) return true;
        using var leave = ct.Register(() => Leave(node));
        return await node.Value.Item2.Task.ConfigureAwait(false);
    }

    public void Release()
    {
        TaskCompletionSource<bool>? next = null;
        lock (sync)
        {
            if (queue.First is { } first)
            {
                queue.RemoveFirst();
                Holder = first.Value.Owner;
                next = first.Value.Go;
            }
            else Holder = null;
        }
        next?.TrySetResult(true);
        Changed?.Invoke();
    }

    /// <summary>A cancelled wait leaves the queue (if it was still in it).</summary>
    private void Leave(LinkedListNode<(Guid, TaskCompletionSource<bool>)> node)
    {
        lock (sync)
        {
            if (node.List != queue) return;
            queue.Remove(node);
        }
        node.Value.Item2.TrySetResult(false);
        Changed?.Invoke();
    }
}
