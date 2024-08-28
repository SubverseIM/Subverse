﻿using Alethic.Kademlia;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using PgpCore;
using Subverse.Abstractions;
using Subverse.Exceptions;
using Subverse.Models;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Text;

using static Subverse.Models.SubverseMessage;

namespace Subverse.Implementations
{
#pragma warning disable CA1416 // Validate platform compatibility
    public class QuicEntityConnection : IEntityConnection
    {
        public static int DEFAULT_CONFIG_START_TTL = 99;

        private readonly QuicStream _quicStream;
        private readonly FileInfo _publicKeyFile, _privateKeyFile;
        private readonly string? _privateKeyPassPhrase;

        private CancellationTokenSource? _cts;
        private Task? _receiveTask;

        private bool disposedValue;

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

        public KNodeId160? LocalConnectionId { get; internal set; }
        public KNodeId160? RemoteConnectionId { get; internal set; }

        public QuicEntityConnection(QuicStream quicStream, FileInfo publicKeyFile, FileInfo privateKeyFile, string? privateKeyPassPhrase)
        {
            _quicStream = quicStream;

            _publicKeyFile = publicKeyFile;
            _privateKeyFile = privateKeyFile;
            _privateKeyPassPhrase = privateKeyPassPhrase;
        }

        public async Task CompleteHandshakeAsync(SubverseEntity self)
        {
            byte[] blobBytes;

            // Initiate handshake by exporting our public key to the remote party
            using (var publicKeyStream = _publicKeyFile.OpenRead())
            using (var quicStreamWriter = new BinaryWriter(_quicStream, Encoding.UTF8, true))
            {
                publicKeyStream.CopyTo(_quicStream);
                publicKeyStream.Position = 0;

                var myKeys = new EncryptionKeys(_publicKeyFile, _privateKeyFile, _privateKeyPassPhrase);

                LocalConnectionId = new(myKeys.PublicKey.GetFingerprint());
                blobBytes = new LocalCertificateCookie(publicKeyStream, myKeys, self).ToBlobBytes();
            }

            // Continue handshake by storing their public key after they do the same
            EncryptionKeys challengeKeys;
            using (var privateKeyStream = _privateKeyFile.OpenRead())
            {
                using var publicKeyStream = Utils.ExtractPGPBlockFromStream(_quicStream, "PUBLIC KEY BLOCK");
                challengeKeys = new EncryptionKeys(publicKeyStream, privateKeyStream, _privateKeyPassPhrase);
            }
            RemoteConnectionId = new(challengeKeys.PublicKey.GetFingerprint());

            // Generate nonce
            byte[] originalNonce = RandomNumberGenerator.GetBytes(64);

            // Encrypt/sign nonce and send it to remote party
            using (var inputNonceStream = new MemoryStream(originalNonce))
            using (var sendNonceStream = new MemoryStream())
            using (var pgp = new PGP(challengeKeys))
            {
                pgp.EncryptAndSign(inputNonceStream, sendNonceStream);
                sendNonceStream.Position = 0;
                sendNonceStream.CopyTo(_quicStream);
            }

            // IMPLICIT: other party receives encrypted nonce, decrypts/verifies, and encrypts/signs to send back to us.
            byte[] receivedNonce;
            using (var outputNonceStream = new MemoryStream())
            using (var pgp = new PGP(challengeKeys))
            {
                using var receivedNonceStream = Utils.ExtractPGPBlockFromStream(_quicStream, "MESSAGE");
                pgp.DecryptAndVerify(receivedNonceStream, outputNonceStream);
                receivedNonce = outputNonceStream.ToArray();
            }

            if (!originalNonce.SequenceEqual(receivedNonce))
            {
                throw new InvalidEntityException($"Connection to entity with ID: \"{RemoteConnectionId}\" could not be verified as authentic!");
            }

            _cts = new CancellationTokenSource();
            _receiveTask = RecieveAsync(_cts.Token);

            // Self-announce to other party
            await SendMessageAsync(new SubverseMessage([LocalConnectionId.Value], DEFAULT_CONFIG_START_TTL, ProtocolCode.Entity, blobBytes));
        }

        internal Task RecieveAsync(CancellationToken cancellationToken)
        {
            return Task.WhenAll([
                Task.Run(() =>
                {
                    using (var bsonReader = new BsonDataReader(_quicStream) { CloseInput = false, SupportMultipleContent = true })
                    {
                        var serializer = new JsonSerializer() { TypeNameHandling = TypeNameHandling.Objects, Converters = { new NodeIdConverter() } };
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            var message = serializer.Deserialize<SubverseMessage>(bsonReader)
                                ?? throw new InvalidOperationException("Expected to recieve SubverseMessage, got malformed data instead!");
                            OnMessageRecieved(new MessageReceivedEventArgs(message));
                            cancellationToken.ThrowIfCancellationRequested();
                            bsonReader.Read();
                        }
                    }
                }, cancellationToken),

                Task.Run(async Task? () =>
                {
                    if(RemoteConnectionId is null || LocalConnectionId is null)
                        throw new InvalidEntityException("No endpoint could be found!");

                    while(!cancellationToken.IsCancellationRequested)
                    {
                        var pingMsg = new SubverseMessage(
                            [LocalConnectionId.Value, RemoteConnectionId.Value],
                            DEFAULT_CONFIG_START_TTL, ProtocolCode.Command,
                            Encoding.UTF8.GetBytes("PING")
                            );
                        await SendMessageAsync(pingMsg);
                        await Task.Delay(5000, cancellationToken);
                    }
                })
            ]);
        }

        public Task SendMessageAsync(SubverseMessage message)
        {
            lock (_quicStream)
            {
                using (var bsonWriter = new BsonDataWriter(_quicStream) { CloseOutput = false, AutoCompleteOnClose = true })
                {
                    var serializer = new JsonSerializer() { TypeNameHandling = TypeNameHandling.Auto, Converters = { new NodeIdConverter() } };
                    serializer.Serialize(bsonWriter, message);
                }
            }

            return Task.CompletedTask;
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
                        _cts?.Dispose();
                        _receiveTask?.Wait();
                    }
                    finally
                    {
                        _quicStream.Dispose();
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
    }
#pragma warning restore CA1416 // Validate platform compatibility
}
