using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RC_Proxy_WATS.Configuration;
using RC_Proxy_WATS.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace RC_Proxy_WATS.Services
{
    public interface IClientConnectionManager : IDisposable
    {
        event Action<string, RcMessage> ClientMessageReceived;
        Task StartListeningAsync(CancellationToken cancellationToken);
        Task SendMessageToAllClientsAsync(RcMessage message);
        Task SendMessageToClientAsync(string clientId, RcMessage message);
        Task SendRewindResponseToClientAsync(string clientId, List<StoredCcgMessage> ccgMessages);
        int ConnectedClientsCount { get; }
    }

    public class ClientConnectionManager : IClientConnectionManager
    {
        private readonly ProxyConfiguration _config;
        private readonly ILogger<ClientConnectionManager> _logger;
        private readonly ConcurrentDictionary<string, ClientConnection> _clients;
        private TcpListener? _listener;
        private bool _disposed;

        public event Action<string, RcMessage>? ClientMessageReceived;

        public int ConnectedClientsCount => _clients.Count;

        public ClientConnectionManager(IOptions<ProxyConfiguration> config, ILogger<ClientConnectionManager> logger)
        {
            _config = config.Value;
            _logger = logger;
            _clients = new ConcurrentDictionary<string, ClientConnection>();
        }

        public async Task StartListeningAsync(CancellationToken cancellationToken)
        {
            if (_listener != null)
                throw new InvalidOperationException("Already listening for client connections");

            try
            {
                var endpoint = IPAddress.Parse(_config.ProxyBindAddress);
                _listener = new TcpListener(endpoint, _config.ProxyListenPort);
                _listener.Start();

                _logger.LogInformation("Started listening for client connections on {Address}:{Port}",
                    _config.ProxyBindAddress, _config.ProxyListenPort);

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var tcpClient = await _listener.AcceptTcpClientAsync();
                        var clientId = Guid.NewGuid().ToString();
                        
                        _logger.LogInformation("New client connection from {RemoteEndPoint}. ClientId: {ClientId}",
                            tcpClient.Client.RemoteEndPoint, clientId);

                        var clientConnection = new ClientConnection(clientId, tcpClient, _logger);
                        _clients.TryAdd(clientId, clientConnection);

                        // Start handling this client
                        _ = Task.Run(async () => await HandleClientAsync(clientConnection, cancellationToken));
                    }
                    catch (ObjectDisposedException)
                    {
                        // Normal shutdown
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error accepting client connection");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting client listener");
                throw;
            }
        }

        private async Task HandleClientAsync(ClientConnection client, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Started handling client {ClientId}", client.ClientId);

                client.MessageReceived += (message) => ClientMessageReceived?.Invoke(client.ClientId, message);
                
                await client.StartListeningAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling client {ClientId}", client.ClientId);
            }
            finally
            {
                _clients.TryRemove(client.ClientId, out _);
                client.Dispose();
                
                _logger.LogInformation("Client {ClientId} disconnected. Remaining clients: {Count}",
                    client.ClientId, _clients.Count);
            }
        }

        public async Task SendMessageToAllClientsAsync(RcMessage message)
        {
            var tasks = new List<Task>();
            
            foreach (var client in _clients.Values)
            {
                tasks.Add(SendMessageToClientSafeAsync(client, message));
            }

            await Task.WhenAll(tasks);
        }

        public async Task SendMessageToClientAsync(string clientId, RcMessage message)
        {
            if (_clients.TryGetValue(clientId, out var client))
            {
                await SendMessageToClientSafeAsync(client, message);
            }
            else
            {
                _logger.LogWarning("Attempted to send message to non-existent client {ClientId}", clientId);
            }
        }

        private async Task SendMessageToClientSafeAsync(ClientConnection client, RcMessage message)
        {
            try
            {
                await client.SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send message to client {ClientId}", client.ClientId);
                
                // Remove disconnected client
                _clients.TryRemove(client.ClientId, out _);
                client.Dispose();
            }
        }

        public async Task SendRewindResponseToClientAsync(string clientId, List<StoredCcgMessage> ccgMessages)
        {
            if (!_clients.TryGetValue(clientId, out var client))
            {
                _logger.LogWarning("Attempted to send rewind response to non-existent client {ClientId}", clientId);
                return;
            }

            try
            {
                _logger.LogInformation("Sending {Count} CCG messages to client {ClientId} for rewind",
                    ccgMessages.Count, clientId);

                // Send each CCG message as a separate RC message
                foreach (var ccgMessage in ccgMessages)
                {
                    var rcMessage = ccgMessage.ToRcMessage();
                    await client.SendMessageAsync(rcMessage);
                }

                // Send rewind complete message
                var rewindCompleteMessage = new RcMessage();
                rewindCompleteMessage.Header.Session = "PROXY";
                rewindCompleteMessage.Header.SequenceNumber = 0;
                
                var rewindCompleteBlock = new RcMessageBlock();
                rewindCompleteBlock.Payload = new byte[] { (byte)'r' }; // Rewind complete
                rewindCompleteBlock.Length = 1;
                rewindCompleteMessage.Blocks.Add(rewindCompleteBlock);

                await client.SendMessageAsync(rewindCompleteMessage);

                _logger.LogInformation("Completed rewind response for client {ClientId}", clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send rewind response to client {ClientId}", clientId);
                
                // Remove disconnected client
                _clients.TryRemove(clientId, out _);
                client.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                _listener?.Stop();
                
                var disconnectTasks = new List<Task>();
                foreach (var client in _clients.Values)
                {
                    disconnectTasks.Add(Task.Run(() => client.Dispose()));
                }

                Task.WaitAll(disconnectTasks.ToArray(), TimeSpan.FromSeconds(5));
                _clients.Clear();

                _logger.LogInformation("Client connection manager disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing client connection manager");
            }
            finally
            {
                _disposed = true;
            }
        }
    }

    internal class ClientConnection : IDisposable
    {
        private readonly TcpClient _tcpClient;
        private readonly NetworkStream _stream;
        private readonly ILogger _logger;
        private bool _disposed;

        public string ClientId { get; }
        public bool IsConnected => _tcpClient?.Connected == true;

        public event Action<RcMessage>? MessageReceived;

        public ClientConnection(string clientId, TcpClient tcpClient, ILogger logger)
        {
            ClientId = clientId;
            _tcpClient = tcpClient;
            _stream = tcpClient.GetStream();
            _logger = logger;
        }

        public async Task StartListeningAsync(CancellationToken cancellationToken)
        {
            byte[] headerBuffer = new byte[RcHeader.HeaderSize];

            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                try
                {
                    // Read header
                    int headerBytesRead = 0;
                    while (headerBytesRead < RcHeader.HeaderSize)
                    {
                        int bytesRead = await _stream.ReadAsync(
                            headerBuffer,
                            headerBytesRead,
                            RcHeader.HeaderSize - headerBytesRead,
                            cancellationToken);

                        if (bytesRead == 0)
                        {
                            _logger.LogInformation("Client {ClientId} disconnected", ClientId);
                            return;
                        }

                        headerBytesRead += bytesRead;
                    }

                    // Parse header
                    RcHeader header = RcHeader.FromBytes(headerBuffer);

                    // Handle heartbeat
                    if (header.BlockCount == 0)
                    {
                        var heartbeatMessage = new RcMessage { Header = header };
                        MessageReceived?.Invoke(heartbeatMessage);
                        continue;
                    }

                    // Read message blocks
                    byte[] fullMessageBuffer = new byte[1024 * 1024]; // 1MB buffer
                    Array.Copy(headerBuffer, fullMessageBuffer, RcHeader.HeaderSize);
                    
                    int totalBytesRead = RcHeader.HeaderSize;

                    // Read each block
                    for (int i = 0; i < header.BlockCount; i++)
                    {
                        // Read block header
                        int blockHeaderBytesRead = 0;
                        while (blockHeaderBytesRead < RcMessageBlock.BlockHeaderSize)
                        {
                            int bytesRead = await _stream.ReadAsync(
                                fullMessageBuffer,
                                totalBytesRead,
                                RcMessageBlock.BlockHeaderSize - blockHeaderBytesRead,
                                cancellationToken);

                            if (bytesRead == 0)
                                return;

                            blockHeaderBytesRead += bytesRead;
                            totalBytesRead += bytesRead;
                        }

                        // Get block length and read payload
                        ushort blockLength = BitConverter.ToUInt16(fullMessageBuffer, totalBytesRead - RcMessageBlock.BlockHeaderSize);

                        int blockPayloadBytesRead = 0;
                        while (blockPayloadBytesRead < blockLength)
                        {
                            int bytesRead = await _stream.ReadAsync(
                                fullMessageBuffer,
                                totalBytesRead,
                                blockLength - blockPayloadBytesRead,
                                cancellationToken);

                            if (bytesRead == 0)
                                return;

                            blockPayloadBytesRead += bytesRead;
                            totalBytesRead += bytesRead;
                        }
                    }

                    // Parse complete message
                    byte[] messageData = new byte[totalBytesRead];
                    Array.Copy(fullMessageBuffer, messageData, totalBytesRead);
                    
                    var message = RcMessage.FromBytes(messageData);
                    MessageReceived?.Invoke(message);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Error receiving message from client {ClientId}", ClientId);
                    break;
                }
            }
        }

        public async Task SendMessageAsync(RcMessage message)
        {
            if (!IsConnected)
                throw new InvalidOperationException($"Client {ClientId} is not connected");

            byte[] data = message.ToBytes();
            await _stream.WriteAsync(data, 0, data.Length);
            await _stream.FlushAsync();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                _stream?.Close();
                _tcpClient?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing client connection {ClientId}", ClientId);
            }
            finally
            {
                _stream?.Dispose();
                _tcpClient?.Dispose();
                _disposed = true;
            }
        }
    }
}