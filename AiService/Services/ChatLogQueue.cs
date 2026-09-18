using System.Threading.Channels;
using AiService.Models;

namespace AiService.Services
{
    // Chat responses must not wait on an Oracle write. ChatController enqueues the
    // finished ChatTrace here; ChatLogWriter drains it on a background thread.
    public class ChatLogQueue
    {
        private readonly Channel<ChatTrace> _channel = Channel.CreateBounded<ChatTrace>(
            new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest });

        public void Enqueue(ChatTrace trace) => _channel.Writer.TryWrite(trace);

        public IAsyncEnumerable<ChatTrace> ReadAllAsync(CancellationToken ct) =>
            _channel.Reader.ReadAllAsync(ct);
    }
}
