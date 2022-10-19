using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Orleans.Networking.Shared;


namespace Orleans.Runtime.Development
{
    public class StreamConnectionManager: IStreamConnectionManager 
    {
        private Stream connectionA;
        private Stream connectionB;

        public Stream GetConnectionA() => this.connectionA;
        public Stream GetConnectionB() => this.connectionB;


        public StreamConnectionManager(Stream connA, Stream connB)
        {
            connectionA = connA;
            connectionB = connB;
        }
    }

    public interface IStreamConnectionManager
    {
        public Stream GetConnectionA();
        public Stream GetConnectionB();

    }

    public class InMemoryTransportConnection : TransportConnection
    {
        private readonly CancellationTokenSource _connectionClosedTokenSource = new();
        private readonly ILogger _logger;
        private bool _isClosed;
        private readonly TaskCompletionSource<bool> _waitForCloseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private InMemoryTransportConnection(MemoryPool<byte> memoryPool, ILogger logger, DuplexPipe.DuplexPipePair pair, EndPoint localEndPoint, EndPoint remoteEndPoint)
        {
            MemoryPool = memoryPool;
            _logger = logger;

            LocalEndPoint = localEndPoint;
            RemoteEndPoint = remoteEndPoint;

            Application = pair.Application;
            Transport = pair.Transport;

            ConnectionClosed = _connectionClosedTokenSource.Token;
        }

        public static InMemoryTransportConnection Create(MemoryPool<byte> memoryPool, ILogger logger, EndPoint localEndPoint, EndPoint remoteEndPoint, IStreamConnectionManager connManager)
        {
            /*Stream s1 = connManager.GetConnectionA();
            PipeReader pr1 = PipeReader.Create(s1);
            PipeWriter pw1 = PipeWriter.Create(s1);
            var pipe1 = new DuplexPipe(pr1, pw1);

            Stream s2 = connManager.GetConnectionB();
            PipeReader pr2 = PipeReader.Create(s2);
            PipeWriter pw2 = PipeWriter.Create(s2);
            var pipe2 = new DuplexPipe(pr2, pw2);

            var pair = new DuplexPipe.DuplexPipePair(transport: pipe1, application: pipe2);*/

            var pair = DuplexPipe.CreateConnectionPair(
                    new PipeOptions(memoryPool, readerScheduler: PipeScheduler.Inline, useSynchronizationContext: false),
                    new PipeOptions(memoryPool, writerScheduler: PipeScheduler.Inline, useSynchronizationContext: false));
            
            return new InMemoryTransportConnection(memoryPool, logger, pair, localEndPoint, remoteEndPoint);
        }

        public static InMemoryTransportConnection Create(MemoryPool<byte> memoryPool, ILogger logger, InMemoryTransportConnection other, EndPoint localEndPoint, IStreamConnectionManager connManager)
        {
            // Swap the application & tranport pipes since we're going in the other direction.

            /*Stream s1 = connManager.GetConnectionA();
            PipeReader pr1 = PipeReader.Create(s1);
            PipeWriter pw1 = PipeWriter.Create(s1);
            var pipe1 = new DuplexPipe(pr1, pw1);

            Stream s2 = connManager.GetConnectionB();
            PipeReader pr2 = PipeReader.Create(s2);
            PipeWriter pw2 = PipeWriter.Create(s2);
            var pipe2 = new DuplexPipe(pr2, pw2);

            var pair = new DuplexPipe.DuplexPipePair(transport: pipe2, application: pipe1);*/

            var pair = new DuplexPipe.DuplexPipePair(transport: other.Application, application: other.Transport);
            var remoteEndPoint = other.LocalEndPoint;
            return new InMemoryTransportConnection(memoryPool, logger, pair, localEndPoint, remoteEndPoint);
        }

        public PipeWriter Input => Application.Output;

        public PipeReader Output => Application.Input;

        public override MemoryPool<byte> MemoryPool { get; }

        public ConnectionAbortedException AbortReason { get; private set; }

        public Task WaitForCloseTask => _waitForCloseTcs.Task;

        public override void Abort(ConnectionAbortedException abortReason)
        {
            _logger.LogDebug(@"Connection id ""{ConnectionId}"" closing because: ""{Message}""", ConnectionId, abortReason?.Message);

            Input.Complete(abortReason);

            OnClosed();

            AbortReason = abortReason;
        }

        public void OnClosed()
        {
            if (_isClosed)
            {
                return;
            }

            _isClosed = true;
            /*
            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                state._connectionClosedTokenSource.Cancel();

                state._waitForCloseTcs.TrySetResult(true);
            },
            this,
            preferLocal: false);*/
        }

        public override async ValueTask DisposeAsync()
        {
            Transport.Input.Complete();
            Transport.Output.Complete();

            await _waitForCloseTcs.Task;

            _connectionClosedTokenSource.Dispose();
        }
    }
}
