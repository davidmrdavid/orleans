using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using Orleans.Networking.Shared;
using Microsoft.AspNetCore.Connections;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.IO;

namespace Orleans.Runtime.Hosting
{
    public class ServerlessConnection : TransportConnection
    {
        private readonly CancellationTokenSource _connectionClosedTokenSource = new();
        private readonly ILogger _logger;
        private bool _isClosed;
        private readonly TaskCompletionSource<bool> _waitForCloseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PipeWriter Input => Application.Output;

        public PipeReader Output => Application.Input;

        public override MemoryPool<byte> MemoryPool { get; }

        public ConnectionAbortedException AbortReason { get; private set; }

        public Task WaitForCloseTask => _waitForCloseTcs.Task;

        private ServerlessConnection(MemoryPool<byte> memoryPool, ILogger logger, DuplexPipe.DuplexPipePair pair, EndPoint localEndPoint, EndPoint remoteEndPoint)
        {
            MemoryPool = memoryPool;
            _logger = logger;

            LocalEndPoint = localEndPoint;
            RemoteEndPoint = remoteEndPoint;

            Application = pair.Application;
            Transport = pair.Transport;

            ConnectionClosed = _connectionClosedTokenSource.Token;
        }

        public static ServerlessConnection Create(MemoryPool<byte> memoryPool, ILogger logger, EndPoint localEndPoint, EndPoint remoteEndPoint)
        {
            Stream s = null;
            PipeReader pr = PipeReader.Create(s);
            PipeWriter pw = PipeWriter.Create(s);
            var pipe = new DuplexPipe(pr, pw);

            var pair = DuplexPipe.CreateConnectionPair(
                    new PipeOptions(memoryPool, readerScheduler: PipeScheduler.Inline, useSynchronizationContext: false),
                    new PipeOptions(memoryPool, writerScheduler: PipeScheduler.Inline, useSynchronizationContext: false));
            return new ServerlessConnection(memoryPool, logger, pair, localEndPoint, remoteEndPoint);
        }

        public static ServerlessConnection Create(MemoryPool<byte> memoryPool, ILogger logger, ServerlessConnection other, EndPoint localEndPoint)
        {
            // Swap the application & tranport pipes since we're going in the other direction.
            var pair = new DuplexPipe.DuplexPipePair(transport: other.Application, application: other.Transport);
            var remoteEndPoint = other.LocalEndPoint;
            return new ServerlessConnection(memoryPool, logger, pair, localEndPoint, remoteEndPoint);

        }

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
