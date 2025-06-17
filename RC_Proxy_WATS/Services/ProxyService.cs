using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RC_Proxy_WATS.Models;

namespace RC_Proxy_WATS.Services
{
    public class ProxyService : BackgroundService
    {
        private readonly IRcConnectionService _rcConnection;
        private readonly IClientConnectionManager _clientManager;
        private readonly IRabbitMqService _rabbitMqService;
        private readonly ICcgMessageStore _ccgMessageStore;
        private readonly ILogger<ProxyService> _logger;

        public ProxyService(
            IRcConnectionService rcConnection,
            IClientConnectionManager clientManager,
            IRabbitMqService rabbitMqService,
            ICcgMessageStore ccgMessageStore,
            ILogger<ProxyService> logger)
        {
            _rcConnection = rcConnection;
            _clientManager = clientManager;
            _rabbitMqService = rabbitMqService;
            _ccgMessageStore = ccgMessageStore;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting RC_Proxy_WATS service...");

            try
            {
                // Initialize RabbitMQ
                await _rabbitMqService.InitializeAsync();
                _logger.LogInformation("RabbitMQ initialized successfully");

                // Initialize CCG message store from RabbitMQ
                await _ccgMessageStore.InitializeFromRabbitMqAsync();
                _logger.LogInformation("CCG message store initialized successfully");

                // Setup event handlers
                SetupEventHandlers();

                // Connect to RC server
                await _rcConnection.ConnectAsync();
                _logger.LogInformation("Connected to RC server successfully");

                // Start tasks concurrently
                var rcListeningTask = _rcConnection.StartListeningAsync(stoppingToken);
                var clientListeningTask = _clientManager.StartListeningAsync(stoppingToken);
                var maintenanceTask = StartMaintenanceTaskAsync(stoppingToken);

                _logger.LogInformation("RC_Proxy_WATS service started successfully");

                // Wait for any task to complete (which would indicate an error or shutdown)
                await Task.WhenAny(rcListeningTask, clientListeningTask, maintenanceTask);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in proxy service execution");
                throw;
            }
            finally
            {
                _logger.LogInformation("RC_Proxy_WATS service is shutting down...");
                await CleanupAsync();
            }
        }

        private void SetupEventHandlers()
        {
            // Handle RC connection status changes
            _rcConnection.ConnectionStatusChanged += OnRcConnectionStatusChanged;

            // Handle messages from RC server
            _rcConnection.MessageReceived += OnRcMessageReceived;

            // Handle messages from clients
            _clientManager.ClientMessageReceived += OnClientMessageReceived;
        }

        private void OnRcConnectionStatusChanged(bool isConnected)
        {
            if (isConnected)
            {
                _logger.LogInformation("RC server connection established");
            }
            else
            {
                _logger.LogWarning("RC server connection lost");
                // TODO: Implement reconnection logic
            }
        }

        private async void OnRcMessageReceived(RcMessage message)
        {
            try
            {
                // Check if this is a CCG message (type 'B')
                if (message.IsCcgMessage)
                {
                    await HandleCcgMessageFromRc(message);
                }

                // Forward all messages to clients (including CCG messages)
                await _clientManager.SendMessageToAllClientsAsync(message);

                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Forwarded RC message to {ClientCount} clients. Session: {Session}, Sequence: {Sequence}",
                        _clientManager.ConnectedClientsCount, message.Header.Session, message.Header.SequenceNumber);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing RC message. Session: {Session}, Sequence: {Sequence}",
                    message.Header.Session, message.Header.SequenceNumber);
            }
        }

        private async Task HandleCcgMessageFromRc(RcMessage message)
        {
            try
            {
                var storedMessage = StoredCcgMessage.FromRcMessage(message);
                await _ccgMessageStore.AddCcgMessageAsync(storedMessage);

                _logger.LogDebug("Stored CCG message. Sequence: {Sequence}, Total stored: {TotalCount}",
                    storedMessage.SequenceNumber, _ccgMessageStore.MessageCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store CCG message. Sequence: {Sequence}",
                    message.Header.SequenceNumber);
                // Don't throw - we should still forward the message to clients
            }
        }

        private async void OnClientMessageReceived(string clientId, RcMessage message)
        {
            try
            {
                // Check if this is a rewind request
                if (message.IsRewindRequest)
                {
                    await HandleRewindRequest(clientId, message);
                }
                else
                {
                    // Forward all other messages to RC server
                    await _rcConnection.SendMessageAsync(message);

                    _logger.LogTrace("Forwarded client message to RC server. ClientId: {ClientId}, Session: {Session}, Sequence: {Sequence}",
                        clientId, message.Header.Session, message.Header.SequenceNumber);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing client message. ClientId: {ClientId}, Session: {Session}, Sequence: {Sequence}",
                    clientId, message.Header.Session, message.Header.SequenceNumber);
            }
        }

        private async Task HandleRewindRequest(string clientId, RcMessage message)
        {
            try
            {
                _logger.LogInformation("Processing rewind request from client {ClientId}", clientId);

                // Extract the last seen sequence number from the rewind request
                uint lastSeenSequence = 0;
                if (message.Blocks.Count > 0 && message.Blocks[0].Payload.Length >= 5)
                {
                    lastSeenSequence = BitConverter.ToUInt32(message.Blocks[0].Payload, 1);
                }

                // Get CCG messages from store
                List<StoredCcgMessage> ccgMessages;
                if (lastSeenSequence == 0)
                {
                    // Client wants all messages
                    ccgMessages = await _ccgMessageStore.GetAllCcgMessagesAsync();
                }
                else
                {
                    // Client wants messages from specific sequence
                    ccgMessages = await _ccgMessageStore.GetCcgMessagesFromSequenceAsync(lastSeenSequence);
                }

                _logger.LogInformation("Sending {Count} CCG messages to client {ClientId} for rewind (from sequence {LastSeen})",
                    ccgMessages.Count, clientId, lastSeenSequence);

                // Send the messages to the client
                await _clientManager.SendRewindResponseToClientAsync(clientId, ccgMessages);

                _logger.LogInformation("Completed rewind request for client {ClientId}", clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle rewind request for client {ClientId}", clientId);

                // Send an error response
                try
                {
                    var errorMessage = new RcMessage();
                    errorMessage.Header.Session = "PROXY";
                    errorMessage.Header.SequenceNumber = 0;

                    var errorBlock = new RcMessageBlock();
                    var errorText = $"Rewind failed: {ex.Message}";
                    var errorBytes = System.Text.Encoding.ASCII.GetBytes(errorText);
                    errorBlock.Payload = new byte[3 + errorText.Length];
                    errorBlock.Payload[0] = (byte)'E'; // Error message
                    BitConverter.GetBytes((ushort)errorText.Length).CopyTo(errorBlock.Payload, 1);
                    errorBytes.CopyTo(errorBlock.Payload, 3);
                    errorBlock.Length = (ushort)errorBlock.Payload.Length;

                    errorMessage.Blocks.Add(errorBlock);

                    await _clientManager.SendMessageToClientAsync(clientId, errorMessage);
                }
                catch (Exception errorEx)
                {
                    _logger.LogError(errorEx, "Failed to send error response to client {ClientId}", clientId);
                }
            }
        }

        private async Task StartMaintenanceTaskAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting maintenance task");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for maintenance interval (every 5 minutes)
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

                    // Perform maintenance tasks
                    await PerformMaintenanceAsync();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during maintenance task");
                }
            }

            _logger.LogInformation("Maintenance task stopped");
        }

        private async Task PerformMaintenanceAsync()
        {
            try
            {
                // Clean old messages from RabbitMQ
                await _rabbitMqService.ClearOldMessagesAsync();

                // Log statistics
                var messageCount = await _rabbitMqService.GetMessageCountAsync();
                var cacheStats = ((CcgMessageStore)_ccgMessageStore).GetCacheStatistics();

                _logger.LogInformation("Maintenance completed. RabbitMQ messages: {RabbitMqCount}, " +
                    "Cache messages: {CacheCount}, Connected clients: {ClientCount}",
                    messageCount, cacheStats.MessageCount, _clientManager.ConnectedClientsCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during maintenance");
            }
        }

        private async Task CleanupAsync()
        {
            try
            {
                await _rcConnection.DisconnectAsync();
                _logger.LogInformation("Disconnected from RC server");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disconnecting from RC server");
            }
        }

        public override void Dispose()
        {
            try
            {
                _rcConnection?.Dispose();
                _clientManager?.Dispose();
                _rabbitMqService?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing proxy service");
            }
            finally
            {
                base.Dispose();
            }
        }
    }
}