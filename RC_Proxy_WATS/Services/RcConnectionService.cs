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
        bool IsInitialized { get; }
        event Action<bool> ConnectionStatusChanged;
        event Action<RcMessage> MessageReceived;
        event Action InitializationCompleted;
        Task ConnectAsync();
        Task DisconnectAsync();
        Task SendMessageAsync(RcMessage message);
        Task StartListeningAsync(CancellationToken cancellationToken);
        Task InitializeWithRewindAsync();
    }

    public class RcConnectionService : IRcConnectionService
    {
        private readonly ProxyConfiguration _config;
        private readonly ILogger<RcConnectionService> _logger;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private bool _isConnected;
        private bool _isInitialized;
        private bool _disposed;
        private CancellationTokenSource? _listeningCancellation;
        private TaskCompletionSource<bool>? _initializationCompletionSource;

        public bool IsConnected => _isConnected && _client?.Connected == true;
        public bool IsInitialized => _isInitialized;

        public event Action<bool>? ConnectionStatusChanged;
        public event Action<RcMessage>? MessageReceived;
        public event Action? InitializationCompleted;

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

        public async Task InitializeWithRewindAsync()
        {
            if (!IsConnected)
                throw new InvalidOperationException("Must be connected to RC server before initialization");

            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Starting RC server initialization with rewind...");

                // Create completion source for initialization
                _initializationCompletionSource = new TaskCompletionSource<bool>();

                // Send rewind request to get all CCG messages
                var rewindMessage = CreateRewindRequestMessage(0); // 0 = get all messages
                await SendMessageAsync(rewindMessage);

                _logger.LogInformation("Sent rewind request to RC server, waiting for historical data...");

                // Wait for initialization to complete (with timeout)
                var initializationTask = _initializationCompletionSource.Task;
                var timeoutTask = Task.Delay(TimeSpan.FromMinutes(_config.InitializationTimeoutMinutes));

                var completedTask = await Task.WhenAny(initializationTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    throw new TimeoutException($"RC server initialization timed out after {_config.InitializationTimeoutMinutes} minutes");
                }

                if (await initializationTask)
                {
                    _isInitialized = true;
                    _logger.LogInformation("RC server initialization completed successfully");
                    InitializationCompleted?.Invoke();
                }
                else
                {
                    throw new InvalidOperationException("RC server initialization failed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize RC server connection");
                throw;
            }
            finally
            {
                _initializationCompletionSource = null;
            }
        }

        private RcMessage CreateRewindRequestMessage(uint lastSeenSequence)
        {
            var message = new RcMessage();
            message.Header.Session = "PROXY";
            message.Header.SequenceNumber = 0; // Control message

            var block = new RcMessageBlock();
            block.Payload = new byte[5]; // 'R' + 4 bytes for sequence number
            block.Payload[0] = (byte)'R';
            BitConverter.GetBytes(lastSeenSequence).CopyTo(block.Payload, 1);
            block.Length = 5;

            message.Blocks.Add(block);

            return message;
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
                    
                    // Check if this is a rewind complete message during initialization
                    if (!_isInitialized && _initializationCompletionSource != null)
                    {
                        if (IsRewindCompleteMessage(message))
                        {
                            _logger.LogInformation("Received rewind complete message from RC server");
                            _initializationCompletionSource.SetResult(true);
                        }
                    }
                    
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

        private bool IsRewindCompleteMessage(RcMessage message)
        {
            return message.Blocks.Any(b => 
                b.Payload.Length > 0 && 
                b.Payload[0] == (byte)'r'); // Rewind complete message
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                // Cancel initialization if in progress
                _initializationCompletionSource?.SetResult(false);
                
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
                _initializationCompletionSource = null;
                _stream?.Dispose();
                _client?.Dispose();
                _disposed = true;
            }
        }
    }
}