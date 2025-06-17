using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RC_Proxy_WATS.Configuration;
using RC_Proxy_WATS.Models;
using System.Net.Sockets;

namespace RC_Proxy_WATS.Services
{
    public interface IRcConnectionService : IDisposable
    {
        bool IsConnected { get; }
        event Action<bool> ConnectionStatusChanged;
        event Action<RcMessage> MessageReceived;
        Task ConnectAsync();
        Task DisconnectAsync();
        Task SendMessageAsync(RcMessage message);
        Task StartListeningAsync(CancellationToken cancellationToken);
    }

    public class RcConnectionService : IRcConnectionService
    {
        private readonly ProxyConfiguration _config;
        private readonly ILogger<RcConnectionService> _logger;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private bool _isConnected;
        private bool _disposed;
        private CancellationTokenSource? _listeningCancellation;

        public bool IsConnected => _isConnected && _client?.Connected == true;

        public event Action<bool>? ConnectionStatusChanged;
        public event Action<RcMessage>? MessageReceived;

        public RcConnectionService(IOptions<ProxyConfiguration> config, ILogger<RcConnectionService> logger)
        {
            _config = config.Value;
            _logger = logger;
        }

        public async Task ConnectAsync()
        {
            if (IsConnected)
                return;

            try
            {
                _logger.LogInformation("Connecting to RC server at {Host}:{Port}...", 
                    _config.RcServerHost, _config.RcServerPort);

                _client = new TcpClient();
                _client.ReceiveTimeout = _config.ConnectionTimeoutSeconds * 1000;
                _client.SendTimeout = _config.ConnectionTimeoutSeconds * 1000;

                await _client.ConnectAsync(_config.RcServerHost, _config.RcServerPort);
                _stream = _client.GetStream();
                
                _isConnected = true;
                ConnectionStatusChanged?.Invoke(true);
                
                _logger.LogInformation("Successfully connected to RC server");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to RC server at {Host}:{Port}", 
                    _config.RcServerHost, _config.RcServerPort);
                
                await DisconnectAsync();
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            if (!_isConnected)
                return;

            try
            {
                _logger.LogInformation("Disconnecting from RC server...");

                // Stop listening
                _listeningCancellation?.Cancel();
                
                // Close connection
                _stream?.Close();
                _client?.Close();
                
                _isConnected = false;
                ConnectionStatusChanged?.Invoke(false);
                
                _logger.LogInformation("Disconnected from RC server");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during RC server disconnection");
            }
            finally
            {
                _stream?.Dispose();
                _client?.Dispose();
                _stream = null;
                _client = null;
            }
        }

        public async Task SendMessageAsync(RcMessage message)
        {
            if (!IsConnected || _stream == null)
                throw new InvalidOperationException("Not connected to RC server");

            try
            {
                byte[] data = message.ToBytes();
                await _stream.WriteAsync(data, 0, data.Length);
                await _stream.FlushAsync();

                if (_config.EnableDebugLogging)
                {
                    _logger.LogTrace("Sent message to RC server. Session: {Session}, Sequence: {Sequence}, Blocks: {BlockCount}",
                        message.Header.Session, message.Header.SequenceNumber, message.Header.BlockCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send message to RC server");
                await DisconnectAsync();
                throw;
            }
        }

        public async Task StartListeningAsync(CancellationToken cancellationToken)
        {
            if (!IsConnected || _stream == null)
                throw new InvalidOperationException("Not connected to RC server");

            _listeningCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _listeningCancellation.Token;

            _logger.LogInformation("Started listening for messages from RC server");

            try
            {
                await ReceiveMessagesAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _logger.LogInformation("RC server message listening cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while listening for RC messages");
                await DisconnectAsync();
                throw;
            }
        }

        private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
        {
            if (_stream == null)
                return;

            byte[] headerBuffer = new byte[RcHeader.HeaderSize];

            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                try
                {
                    // Read header
                    int headerBytesRead = 0;
                    while (headerBytesRead < RcHeader.HeaderSize && !cancellationToken.IsCancellationRequested)
                    {
                        int bytesRead = await _stream.ReadAsync(
                            headerBuffer, 
                            headerBytesRead, 
                            RcHeader.HeaderSize - headerBytesRead,
                            cancellationToken);

                        if (bytesRead == 0)
                        {
                            _logger.LogWarning("RC server closed connection");
                            await DisconnectAsync();
                            return;
                        }

                        headerBytesRead += bytesRead;
                    }

                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Parse header
                    RcHeader header = RcHeader.FromBytes(headerBuffer);

                    // If it's just a heartbeat, process it immediately
                    if (header.BlockCount == 0)
                    {
                        var heartbeatMessage = new RcMessage { Header = header };
                        MessageReceived?.Invoke(heartbeatMessage);
                        
                        if (_config.EnableDebugLogging)
                        {
                            _logger.LogTrace("Received heartbeat from RC server. Session: {Session}", header.Session);
                        }
                        continue;
                    }

                    // Read message blocks
                    byte[] fullMessageBuffer = new byte[1024 * 1024]; // 1MB buffer
                    Array.Copy(headerBuffer, fullMessageBuffer, RcHeader.HeaderSize);
                    
                    int totalBytesRead = RcHeader.HeaderSize;

                    // Read each block
                    for (int i = 0; i < header.BlockCount && !cancellationToken.IsCancellationRequested; i++)
                    {
                        // Read block header (2 bytes for length)
                        int blockHeaderBytesRead = 0;
                        while (blockHeaderBytesRead < RcMessageBlock.BlockHeaderSize)
                        {
                            int bytesRead = await _stream.ReadAsync(
                                fullMessageBuffer,
                                totalBytesRead,
                                RcMessageBlock.BlockHeaderSize - blockHeaderBytesRead,
                                cancellationToken);

                            if (bytesRead == 0)
                            {
                                _logger.LogWarning("RC server closed connection while reading block header");
                                await DisconnectAsync();
                                return;
                            }

                            blockHeaderBytesRead += bytesRead;
                            totalBytesRead += bytesRead;
                        }

                        // Get block length
                        ushort blockLength = BitConverter.ToUInt16(fullMessageBuffer, totalBytesRead - RcMessageBlock.BlockHeaderSize);

                        // Read block payload
                        int blockPayloadBytesRead = 0;
                        while (blockPayloadBytesRead < blockLength && !cancellationToken.IsCancellationRequested)
                        {
                            int bytesRead = await _stream.ReadAsync(
                                fullMessageBuffer,
                                totalBytesRead,
                                blockLength - blockPayloadBytesRead,
                                cancellationToken);

                            if (bytesRead == 0)
                            {
                                _logger.LogWarning("RC server closed connection while reading block payload");
                                await DisconnectAsync();
                                return;
                            }

                            blockPayloadBytesRead += bytesRead;
                            totalBytesRead += bytesRead;
                        }
                    }

                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Parse complete message
                    byte[] messageData = new byte[totalBytesRead];
                    Array.Copy(fullMessageBuffer, messageData, totalBytesRead);
                    
                    var message = RcMessage.FromBytes(messageData);
                    
                    if (_config.EnableDebugLogging)
                    {
                        _logger.LogTrace("Received message from RC server. Session: {Session}, Sequence: {Sequence}, Blocks: {BlockCount}",
                            message.Header.Session, message.Header.SequenceNumber, message.Header.BlockCount);
                    }

                    MessageReceived?.Invoke(message);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Error receiving message from RC server");
                    await DisconnectAsync();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                _listeningCancellation?.Cancel();
                DisconnectAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing RC connection service");
            }
            finally
            {
                _listeningCancellation?.Dispose();
                _stream?.Dispose();
                _client?.Dispose();
                _disposed = true;
            }
        }
    }
}