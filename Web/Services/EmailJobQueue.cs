using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Web.Services
{
    /// <summary>
    /// In-memory queue for signaling the <see cref="EmailJobProcessor"/> that a new job is ready.
    /// Registered as a singleton so both controllers and the processor share the same instance.
    /// </summary>
    public sealed class EmailJobQueue
    {
        private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
            new UnboundedChannelOptions { SingleReader = true }
        );

        public ValueTask EnqueueAsync(Guid jobId, CancellationToken ct = default)
        {
            return _channel.Writer.WriteAsync(jobId, ct);
        }

        public ValueTask<Guid> DequeueAsync(CancellationToken ct)
        {
            return _channel.Reader.ReadAsync(ct);
        }

        /// <summary>
        /// Completes the writer so subsequent <see cref="EnqueueAsync"/> calls fault with
        /// <see cref="System.Threading.Channels.ChannelClosedException"/>. Test-only seam for modeling a
        /// shutdown-time enqueue failure; production never completes the queue.
        /// </summary>
        internal void CompleteWriter() => _channel.Writer.Complete();
    }
}
