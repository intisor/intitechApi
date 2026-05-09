using System.Threading.Channels;

namespace IntitechApi.Services;

public interface IWorktaleQueue
{
    ValueTask EnqueueAsync(string entryId, CancellationToken cancellationToken);
    ValueTask<string> DequeueAsync(CancellationToken cancellationToken);
}

public class WorktaleQueue : IWorktaleQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(string entryId, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(entryId, cancellationToken);

    public ValueTask<string> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}