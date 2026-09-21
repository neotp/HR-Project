using System.Threading.Channels;

namespace HrProject.Api.Services;

public sealed class LotusNotesOutboxSignal
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public void Notify() => channel.Writer.TryWrite(true);

    public async Task WaitAsync(CancellationToken cancellationToken) =>
        await channel.Reader.ReadAsync(cancellationToken);
}
