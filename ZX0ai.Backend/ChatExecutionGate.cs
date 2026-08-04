using ZX0ai.Core.Services;

namespace ZX0ai.Backend;

/// <summary>Caps provider-heavy chat runs without holding request threads.</summary>
internal sealed class ChatExecutionGate
{
    private readonly SemaphoreSlim _semaphore;

    public ChatExecutionGate(IConfigService config)
    {
        var maximum = Math.Clamp(config.Options.Agents.MaxConcurrentAgents, 1, 8);
        _semaphore = new SemaphoreSlim(maximum, maximum);
    }

    internal async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
