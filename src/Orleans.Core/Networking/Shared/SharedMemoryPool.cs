using System.Buffers;

namespace Orleans.Networking.Shared
{
    public sealed class SharedMemoryPool // NOTE: made this public for testing
    {
        public MemoryPool<byte> Pool { get; } = KestrelMemoryPool.Create();
    }
}
