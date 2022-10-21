using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.Networking.Shared;
using Orleans.Runtime;
using Orleans.Runtime.Development;
using Orleans.Runtime.Messaging;

namespace Orleans.TestingHost.InMemoryTransport;

public static class KeyExports
{
    public static object GetSiloConnectionKey => SiloConnectionFactory.ServicesKey;
    public static object GetConnectionListenerKey => SiloConnectionListener.ServicesKey;
    public static object GetGatewayKey => GatewayConnectionListener.ServicesKey;

}

public static class InMemoryTransportExtensions
{
    public static ISiloBuilder UseInMemoryConnectionTransport(this ISiloBuilder siloBuilder, InMemoryTransportConnectionHub hub, IStreamConnectionManager streamConnectionManager)
    {
        Console.WriteLine($":: UseInMemoryConnectiontTransport | {streamConnectionManager.GetName()}");
        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingletonKeyedService<object, IConnectionFactory>(SiloConnectionFactory.ServicesKey, CreateInMemoryConnectionFactory(hub, streamConnectionManager));
            services.AddSingletonKeyedService<object, IConnectionListenerFactory>(SiloConnectionListener.ServicesKey, CreateInMemoryConnectionListenerFactory(hub, streamConnectionManager));
            services.AddSingletonKeyedService<object, IConnectionListenerFactory>(GatewayConnectionListener.ServicesKey, CreateInMemoryConnectionListenerFactory(hub, streamConnectionManager));
        });

        return siloBuilder;
    }

    public static IClientBuilder UseInMemoryConnectionTransport(this IClientBuilder clientBuilder, InMemoryTransportConnectionHub hub, IStreamConnectionManager streamConnectionManager)
    {
        clientBuilder.ConfigureServices(services =>
        {
            services.AddSingletonKeyedService<object, IConnectionFactory>(ClientOutboundConnectionFactory.ServicesKey, CreateInMemoryConnectionFactory(hub, streamConnectionManager));
        });

        return clientBuilder;
    }

    private static Func<IServiceProvider, object, IConnectionFactory> CreateInMemoryConnectionFactory(InMemoryTransportConnectionHub hub, IStreamConnectionManager connectionManager)
    {
        return (IServiceProvider sp, object key) =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var sharedMemoryPool = sp.GetRequiredService<SharedMemoryPool>();
            return new InMemoryTransportConnectionFactory(hub, loggerFactory, sharedMemoryPool, connectionManager);
        };
    }

    private static Func<IServiceProvider, object, IConnectionListenerFactory> CreateInMemoryConnectionListenerFactory(InMemoryTransportConnectionHub hub, IStreamConnectionManager connectionManager)
    {
        return (IServiceProvider sp, object key) =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var sharedMemoryPool = sp.GetRequiredService<SharedMemoryPool>();
            return new InMemoryTransportListener(hub, loggerFactory, sharedMemoryPool, connectionManager);
        };
    }
}

public class InMemoryTransportListener : IConnectionListenerFactory, IConnectionListener
{
    private readonly Channel<(InMemoryTransportConnection Connection, TaskCompletionSource<bool> ConnectionAcceptedTcs)> _acceptQueue = Channel.CreateUnbounded<(InMemoryTransportConnection, TaskCompletionSource<bool>)>();
    private readonly InMemoryTransportConnectionHub _hub;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SharedMemoryPool _memoryPool;
    private readonly CancellationTokenSource _disposedCts = new();
    private readonly IStreamConnectionManager connectionManager;

    public InMemoryTransportListener(InMemoryTransportConnectionHub hub, ILoggerFactory loggerFactory, SharedMemoryPool memoryPool, IStreamConnectionManager connectionManager)
    {
        _hub = hub;
        _loggerFactory = loggerFactory;
        _memoryPool = memoryPool;
        this.connectionManager = connectionManager;
    }

    public CancellationToken OnDisposed => _disposedCts.Token;

    public EndPoint EndPoint { get; set; }

    public async Task ConnectAsync(InMemoryTransportConnection connection)
    {
        Console.WriteLine($":: ConnectAsync  (listener) | {connectionManager.GetName()}");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_acceptQueue.Writer.TryWrite((connection, completion)))
        {
            var connected = await completion.Task;
            if (connected)
            {
                return;
            }
        }

        throw new ConnectionFailedException($"Unable to connect to {EndPoint} because its listener has terminated.");
    }

    public async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($":: AcceptAsync (listener) | {connectionManager.GetName()}");
        if (await _acceptQueue.Reader.WaitToReadAsync(cancellationToken))
        {
            if (_acceptQueue.Reader.TryRead(out var item))
            {
                var remoteConnectionContext = item.Connection;
                var localConnectionContext = InMemoryTransportConnection.Create(
                    _memoryPool.Pool,
                    _loggerFactory.CreateLogger<InMemoryTransportConnection>(),
                    other: remoteConnectionContext,
                    localEndPoint: EndPoint,
                    connectionManager);

                // Set the result to true to indicate that the connection was accepted.
                item.ConnectionAcceptedTcs.TrySetResult(true);

                return localConnectionContext;
            }
        }

        return null;
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($":: BindAsync  (listener) | {connectionManager.GetName()}");
        EndPoint = endpoint;
        _hub.RegisterConnectionListenerFactory(endpoint, this);
        return new ValueTask<IConnectionListener>(this);
    }

    public ValueTask DisposeAsync()
    {
        return UnbindAsync(default);
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        _acceptQueue.Writer.TryComplete();
        while (_acceptQueue.Reader.TryRead(out var item))
        {
            // Set the result to false to indicate that the listener has terminated.
            item.ConnectionAcceptedTcs.TrySetResult(false);
        }

        _disposedCts.Cancel();
        return default;
    }
}

public class InMemoryTransportConnectionHub
{
    private readonly ConcurrentDictionary<EndPoint, InMemoryTransportListener> _listeners = new();

    public static InMemoryTransportConnectionHub Instance { get; } = new();

    public void RegisterConnectionListenerFactory(EndPoint endPoint, InMemoryTransportListener listener)
    {
        _listeners[endPoint] = listener;
        listener.OnDisposed.Register(() =>
        {
            ((IDictionary<EndPoint, InMemoryTransportListener>)_listeners).Remove(new KeyValuePair<EndPoint, InMemoryTransportListener>(endPoint, listener));
        });
    }

    public InMemoryTransportListener GetConnectionListenerFactory(EndPoint endPoint)
    {
        _listeners.TryGetValue(endPoint, out var listener);
        return listener;
    }
}

public class InMemoryTransportConnectionFactory : IConnectionFactory
{
    private readonly InMemoryTransportConnectionHub _hub;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SharedMemoryPool _memoryPool;
    private readonly IPEndPoint _localEndpoint;
    private readonly IStreamConnectionManager _streamConnectionManager;

    internal InMemoryTransportConnectionFactory(InMemoryTransportConnectionHub hub, ILoggerFactory loggerFactory, SharedMemoryPool memoryPool, IStreamConnectionManager connManager)
    {
        _hub = hub;
        _loggerFactory = loggerFactory;
        _memoryPool = memoryPool;
        var myRng = new Random();
        _localEndpoint = new IPEndPoint(IPAddress.Loopback, myRng.Next(1024, ushort.MaxValue - 1024));
        _streamConnectionManager = connManager;
    }

    public async ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($":: ConnectAsync  (factory) | {_streamConnectionManager.GetName()}");
        var listener = _hub.GetConnectionListenerFactory(endpoint);
        if (listener is null)
        {
            throw new ConnectionFailedException($"Unable to connect to endpoint {endpoint} because no such endpoint is currently registered.");
        }

        var connectionContext = InMemoryTransportConnection.Create(
            _memoryPool.Pool,
            _loggerFactory.CreateLogger<InMemoryTransportConnection>(),
            _localEndpoint,
            endpoint,
            _streamConnectionManager);
        await listener.ConnectAsync(connectionContext).WithCancellation(cancellationToken);
        return connectionContext;
    }
}

