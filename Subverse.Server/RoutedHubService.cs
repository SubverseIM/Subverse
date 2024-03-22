﻿using Alethic.Kademlia;
using Alethic.Kademlia.Network;
using Hangfire;
using Subverse.Stun;
using Subverse.Abstractions;
using Subverse.Abstractions.Server;
using Subverse.Implementations;
using Subverse.Models;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Quic;
using System.Net.Security;
using System.Net;

namespace Subverse.Server
{
    internal class RoutedHubService : IHubService
    {
        private const string DEFAULT_SERVICE_HOSTNAME = "default.subverse";

        private readonly IConfiguration _configuration;
        private readonly IKHost<KNodeId256> _kHost;
        private readonly ICookieStorage<KNodeId256> _cookieStorage;
        private readonly IMessageQueue<string> _messageQueue;
        private readonly IPgpKeyProvider _keyProvider;
        private readonly IStunUriProvider _stunUriProvider;

        private readonly string _serviceHostname;

        private readonly ConcurrentDictionary<KNodeId256, Task> _taskMap;
        private readonly ConcurrentDictionary<KNodeId256, CancellationTokenSource> _ctsMap;
        private readonly ConcurrentDictionary<KNodeId256, IEntityConnection> _connectionMap;

        private IPEndPoint? _localEndPoint;

        // Solution from: https://stackoverflow.com/a/321404
        // Adapted for increased performance
        private static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => (x & 1) == 0)
                             .Select(x => byte.Parse(hex.AsSpan().Slice(x, 2), NumberStyles.HexNumber))
                             .ToArray();
        }

        public RoutedHubService(IConfiguration configuration, IKHost<KNodeId256> kHost, ICookieStorage<KNodeId256> cookieStorage, IMessageQueue<string> messageQueue, IPgpKeyProvider keyProvider, IStunUriProvider stunUriProvider)
        {
            _configuration = configuration;
            _serviceHostname = _configuration.GetSection("HubService")
                .GetValue<string>("Hostname") ?? DEFAULT_SERVICE_HOSTNAME;

            _kHost = kHost;
            _cookieStorage = cookieStorage;
            _messageQueue = messageQueue;
            _keyProvider = keyProvider;
            _stunUriProvider = stunUriProvider;

            _taskMap = new ConcurrentDictionary<KNodeId256, Task>();
            _ctsMap = new ConcurrentDictionary<KNodeId256, CancellationTokenSource>();
            _connectionMap = new ConcurrentDictionary<KNodeId256, IEntityConnection>();

            // Schedule queue flushing job
            RecurringJob.AddOrUpdate(
                "Subverse.Server.RoutedHubService.FlushMessagesAsync",
                () => FlushMessagesAsync(CancellationToken.None),
                Cron.Minutely);
        }

        public async Task OpenConnectionAsync(IEntityConnection newConnection)
        {
            var self = await GetSelfAsync();
            await newConnection.CompleteHandshakeAsync(self);

            if (newConnection.ConnectionId is not null)
            {
                var connectionId = newConnection.ConnectionId.Value;

                // Setup connection for routing & message events
                newConnection.MessageReceived += Connection_MessageReceived;
                _ = _connectionMap.AddOrUpdate(connectionId, newConnection,
                    (key, oldConnection) =>
                    {
                        oldConnection.Dispose();
                        return newConnection;
                    });


                // Immediately send all messages we've cached for this particular entity (in the background)
                var newCts = new CancellationTokenSource();
                _ = _ctsMap.AddOrUpdate(connectionId, newCts,
                    (key, oldCts) =>
                    {
                        oldCts.Dispose();
                        return newCts;
                    });

                Func<KNodeId256, Task> newTaskFactory = (key) =>
                    Task.Run(() => FlushMessagesAsync(key, newCts.Token));

                _ = _taskMap.AddOrUpdate(connectionId, newTaskFactory, (key, oldTask) =>
                    {
                        try
                        {
                            oldTask.Wait();
                        }
                        catch (OperationCanceledException) { }

                        return newTaskFactory(key);
                    });
            }
        }

        public async Task CloseConnectionAsync(IEntityConnection connection)
        {
            if (connection.ConnectionId is not null)
            {
                var connectionId = connection.ConnectionId.Value;

                _ctsMap.Remove(connectionId, out CancellationTokenSource? storedCts);
                storedCts?.Dispose();

                _taskMap.Remove(connectionId, out Task? storedTask);
                try
                {
                    if (storedTask is not null) await storedTask;
                }
                catch (OperationCanceledException) { }

                _connectionMap.Remove(connectionId, out IEntityConnection? storedConnection);
                storedConnection?.Dispose();
            }
        }

        public async Task FlushMessagesAsync(KNodeId256 connectionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = await _messageQueue.DequeueByKeyAsync(connectionId.ToString());

            while (message is not null)
            {
                await RouteMessageAsync(connectionId, message);

                cancellationToken.ThrowIfCancellationRequested();
                message = await _messageQueue.DequeueByKeyAsync(connectionId.ToString());
            }
        }

        public async Task FlushMessagesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyedMessage = await _messageQueue.DequeueAsync();

            while (keyedMessage is not null)
            {
                KNodeId256 recipient = new(StringToByteArray(keyedMessage.Key));
                await RouteMessageAsync(recipient, keyedMessage.Message);

                cancellationToken.ThrowIfCancellationRequested();
                keyedMessage = await _messageQueue.DequeueAsync();
            }
        }

        public async Task<SubverseHub> GetSelfAsync()
        {
            var quicRemoteEndPoint = await GetRemoteEndPointAsync();
            var kRemoteEndPoint = await GetRemoteEndPointAsync(30603);

            return new SubverseHub(
                    _serviceHostname,
                    new UriBuilder()
                    {
                        Scheme = "udp",
                        Host = kRemoteEndPoint.Address.ToString(),
                        Port = kRemoteEndPoint.Port
                    }.ToString(),
                    new UriBuilder()
                    {
                        Scheme = "subverse",
                        Host = quicRemoteEndPoint.Address.ToString(),
                        Port = quicRemoteEndPoint.Port
                    }.ToString(),
                    [ /* No owner metadata for now... */ ]
                    );
        }

        public void SetLocalEndPoint(IPEndPoint localEndPoint) 
        {
            _localEndPoint = localEndPoint;
        }

        public async Task<IPEndPoint> GetRemoteEndPointAsync(int? localPortNum = null)
        {
            var stunClient = new StunClientUdp();
            var stunResponse = await Task.WhenAny(
                _stunUriProvider.GetAvailableAsync().Take(8)
                    .Select(uri => stunClient.SendRequestAsync(new StunMessage([]),
                        localPortNum ?? _localEndPoint?.Port ?? 0, uri ?? string.Empty))
                    .ToEnumerable()).Result;

            IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.None, 0);
            foreach (var stunAttr in stunResponse.Attributes)
            {
                switch (stunAttr.Type)
                {
                    case StunAttributeType.MAPPED_ADDRESS:
                        remoteEndPoint = stunAttr.GetMappedAddress();
                        break;
                    case StunAttributeType.XOR_MAPPED_ADDRESS:
                        remoteEndPoint = stunAttr.GetXorMappedAddress();
                        break;
                }
            }

            return remoteEndPoint;
        }

        private async void Connection_MessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            var connection = sender as IEntityConnection;
            if (e.Message.Tags.Length == 1 && e.Message.Tags[0].Equals(connection?.ConnectionId))
            {
                var entityCookie = (CertificateCookie)CertificateCookie.FromBlobBytes(e.Message.Content);
                if (entityCookie.Body is SubverseHub hub)
                {
                    _kHost.RegisterEndpoint(new(hub.KHostUri));
                }

                await _cookieStorage.UpdateAsync(new(entityCookie.Key), entityCookie, default);
            }
            else if (e.Message.Tags.Length > 1)
            {
                await Task.WhenAll(e.Message.Tags.Skip(1)
                    .Select(r => Task.Run(() => RouteMessageAsync(r, e.Message)))
                    );
            }
        }

        private async Task RouteMessageAsync(KNodeId256 recipient, SubverseMessage message)
        {
            if (_connectionMap.TryGetValue(recipient, out IEntityConnection? connection))
            {
                // Forward the message via direct route, since they are already connected to us.
                await (connection?.SendMessageAsync(message) ?? Task.CompletedTask);
            }
            else
            {
                var entityCookie = await _cookieStorage.ReadAsync<CertificateCookie>(new(recipient), default);
                if (entityCookie?.Body is SubverseHub hub)
                {
                    // If this message has a valid TTL value...
                    if (message.TimeToLive > 0)
                    {
                        // Establish connection with remote hub...

#pragma warning disable CA1416 // Validate platform compatibility
                        try
                        {
                            // Try connection w/ 5 second timeout
                            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0)))
                            {
                                var serviceUri = new Uri(hub.ServiceUri);
                                var quicConnection = await QuicConnection.ConnectAsync(
                                    new QuicClientConnectionOptions
                                    {
                                        RemoteEndPoint = new DnsEndPoint(serviceUri.Host, serviceUri.Port),

                                        DefaultStreamErrorCode = 0x0A, // Protocol-dependent error code.
                                        DefaultCloseErrorCode = 0x0B, // Protocol-dependent error code.

                                        ClientAuthenticationOptions = new()
                                        {
                                            ApplicationProtocols = new List<SslApplicationProtocol>() { new("SubverseV1") },
                                            TargetHost = hub.Hostname
                                        },

                                        MaxInboundBidirectionalStreams = 10
                                    }, cts.Token);

                                var hubConnection = new QuicHubConnection(quicConnection, _keyProvider.GetPublicKeyFile(), _keyProvider.GetPrivateKeyFile(), _keyProvider.GetPrivateKeyPassPhrase());
                                await OpenConnectionAsync(hubConnection);
#pragma warning restore CA1416 // Validate platform compatibility

                                // ...and forward the message to it! Decrement TTL because this causes an actual hop!
                                await RouteMessageAsync(recipient, message with { TimeToLive = message.TimeToLive - 1 });
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Our only hopes of contacting this hub have run out!! For now...
                            // Queue this message for future delivery.
                            await _messageQueue.EnqueueAsync(recipient.ToString(), message);
                        }
                    }
                }
                else if (entityCookie?.Body is SubverseUser user)
                {
                    // Forward the message to all of the user's owned nodes
                    await Task.WhenAll(user.OwnedNodes
                        .Select(r => r.RefersTo)
                        .Select(n => Task.Run(() => RouteMessageAsync(n, message)))
                        );
                }
                else if (entityCookie?.Body is SubverseNode node)
                {
                    if (node.MostRecentlySeenBy.RefersTo.Equals(connection?.ServiceId))
                    {
                        // Node was last seen by us, we'd better remember this message so we can (hopefully) eventually send it!
                        await _messageQueue.EnqueueAsync(node.MostRecentlySeenBy.RefersTo.ToString(), message);
                    }
                    else
                    {
                        // Forward message to the hub this node was last seen by
                        await RouteMessageAsync(node.MostRecentlySeenBy.RefersTo, message);
                    }
                }
            }
        }
    }
}
