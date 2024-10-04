﻿using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using Subverse.Abstractions;
using Subverse.Implementations;
using Subverse.Models;
using Subverse.Types;
using System.Collections.Concurrent;
using System.Net.Quic;

namespace Subverse.Server
{
    public class QuicPeerConnection : IPeerConnection
    {
        public static int DEFAULT_CONFIG_START_TTL = 99;

        private readonly QuicConnection _quicConnection;

        private readonly ConcurrentDictionary<SubversePeerId, QuicheStream> _inboundStreamMap;
        private readonly ConcurrentDictionary<SubversePeerId, QuicheStream> _outboundStreamMap;

        private readonly ConcurrentDictionary<SubversePeerId, CancellationTokenSource> _ctsMap;
        private readonly ConcurrentDictionary<SubversePeerId, Task> _taskMap;

        private readonly TaskCompletionSource<SubverseMessage> _initialMessageSource;

        private bool disposedValue;

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

        public QuicPeerConnection(QuicConnection quicConnection)
        {
            _quicConnection = quicConnection;

            _inboundStreamMap = new();
            _outboundStreamMap = new();

            _ctsMap = new();
            _taskMap = new();

            _initialMessageSource = new();
        }

        private QuicheStream? GetBestInboundPeerStream(SubversePeerId peerId)
        {
            QuicheStream? quicheStream;
            if (!_inboundStreamMap.TryGetValue(peerId, out quicheStream))
            {
                quicheStream = _inboundStreamMap.Values.SingleOrDefault();
            }
            return quicheStream;
        }

        private QuicheStream? GetBestOutboundPeerStream(SubversePeerId peerId)
        {
            QuicheStream? quicheStream;
            if (!_inboundStreamMap.TryGetValue(peerId, out quicheStream))
            {
                quicheStream = _outboundStreamMap.Values.SingleOrDefault();
            }
            return quicStream;
        }

        private Task RecieveAsync(QuicStream quicStream, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                using var bsonReader = new BsonDataReader(quicStream)
                {
                    CloseInput = false,
                    SupportMultipleContent = true,
                };

                var serializer = new JsonSerializer()
                {
                    TypeNameHandling = TypeNameHandling.Objects,
                    Converters = { new PeerIdConverter() },
                };

                if (!quicStream.CanRead) throw new NotSupportedException();

                while (!cancellationToken.IsCancellationRequested && quicStream.CanRead)
                {
                    var message = serializer.Deserialize<SubverseMessage>(bsonReader)
                        ?? throw new InvalidOperationException(
                            "Expected to recieve SubverseMessage, " +
                            "got malformed data instead!");

                    _initialMessageSource.TrySetResult(message);
                    OnMessageRecieved(new MessageReceivedEventArgs(message));

                    cancellationToken.ThrowIfCancellationRequested();
                    bsonReader.Read();
                }
            }, cancellationToken);
        }

        protected virtual void OnMessageRecieved(MessageReceivedEventArgs ev)
        {
            MessageReceived?.Invoke(this, ev);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    try
                    {
                        foreach (var (_, cts) in _ctsMap)
                        {
                            if (!cts.IsCancellationRequested)
                            {
                                cts.Dispose();
                            }
                        }

                        Task.WhenAll(_taskMap.Values).Wait();
                    }
                    catch (AggregateException ex) when (ex.InnerExceptions.All(
                        x => x is QuicException ||
                        x is NotSupportedException ||
                        x is OperationCanceledException))
                    { }
                    finally
                    {
                        foreach (var (_, quicStream) in _inboundStreamMap)
                        {
                            quicStream.Dispose();
                        }

                        foreach (var (_, quicStream) in _outboundStreamMap)
                        {
                            quicStream.Dispose();
                        }
                    }
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public async Task<SubversePeerId> CompleteHandshakeAsync(SubverseMessage? message, CancellationToken cancellationToken)
        {
            await _connection.ConnectionEstablished.WaitAsync(cancellationToken);

            QuicheStream inboundStream, outboundStream;
            SubversePeerId recipient;

            CancellationTokenSource newCts;
            Task newTask;

            if (message is null)
            {
                inboundStream = await _connection.AcceptInboundStreamAsync(cancellationToken);
                outboundStream = _connection.GetStream();

                newCts = new();
                newTask = RecieveAsync(inboundStream, newCts.Token);

                SubverseMessage initialMessage = await _initialMessageSource.Task;
                recipient = initialMessage.Recipient;
            }
            else
            {
                outboundStream = _connection.GetStream(_inboundStreamMap.Count);
                inboundStream = await _connection.AcceptInboundStreamAsync(cancellationToken);

                newCts = new();
                newTask = RecieveAsync(inboundStream, newCts.Token);

                recipient = message.Recipient;
            }

            _ = _ctsMap.AddOrUpdate(recipient, newCts,
                    (key, oldCts) =>
                    {
                        if (!oldCts.IsCancellationRequested)
                        {
                            oldCts.Dispose();
                        }

                        return newCts;
                    });

            _ = _taskMap.AddOrUpdate(recipient, newTask,
                (key, oldTask) =>
                {
                    try
                    {
                        oldTask.Wait();
                    }
                    catch (AggregateException ex) when (ex.InnerExceptions.All(
                        x => x is QuicException ||
                        x is NotSupportedException ||
                        x is OperationCanceledException))
                    { }

                    return newTask;
                });

            _ = _inboundStreamMap.AddOrUpdate(recipient, inboundStream,
                (key, oldQuicStream) =>
                {
                    oldQuicStream.Dispose();
                    return inboundStream;
                });

            if (message is not null)
            {
                SendMessage(message);
            }
            else
            {
                // empty message just to get the stream started
                SendMessage(new SubverseMessage(
                    default, DEFAULT_CONFIG_START_TTL, 
                    SubverseMessage.ProtocolCode.Command, 
                    []));
            }

            return recipient;
        }

        public void SendMessage(SubverseMessage message)
        {
            QuicheStream? quicheStream = GetBestOutboundPeerStream(message.Recipient);
            if (quicheStream is null)
            {
                throw new InvalidOperationException("Suitable transport for this message could not be found.");
            }
            else if (!quicStream.CanWrite)
            {
                throw new NotSupportedException("Stream cannot be written to at this time.");
            }

            lock (quicStream)
            {
                using var bsonWriter = new BsonDataWriter(quicStream)
                {
                    CloseOutput = false,
                    AutoCompleteOnClose = true,
                };

                var serializer = new JsonSerializer()
                {
                    TypeNameHandling = TypeNameHandling.Auto,
                    Converters = { new PeerIdConverter() },
                };

                serializer.Serialize(bsonWriter, message);
            }
        }

        public bool HasValidConnectionTo(SubversePeerId peerId)
        {
            QuicheStream? quicheStream = GetBestInboundPeerStream(peerId);
            return quicheStream?.CanRead & quicheStream?.CanWrite ?? false;
        }
    }
}
