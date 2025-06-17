using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RC_Proxy_WATS.Configuration;
using RC_Proxy_WATS.Models;
using System.Text;
using System.Text.Json;

namespace RC_Proxy_WATS.Services
{
    public interface IRabbitMqService : IDisposable
    {
        Task InitializeAsync();
        Task PublishCcgMessageAsync(StoredCcgMessage message);
        Task<List<StoredCcgMessage>> GetAllCcgMessagesAsync();
        Task<int> GetMessageCountAsync();
        Task ClearOldMessagesAsync();
    }

    public class RabbitMqService : IRabbitMqService
    {
        private readonly RabbitMqConfiguration _config;
        private readonly ILogger<RabbitMqService> _logger;
        private IConnection? _connection;
        private IModel? _channel;
        private bool _disposed = false;

        public RabbitMqService(IOptions<RabbitMqConfiguration> config, ILogger<RabbitMqService> logger)
        {
            _config = config.Value;
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            try
            {
                var factory = new ConnectionFactory()
                {
                    HostName = _config.HostName,
                    Port = _config.Port,
                    UserName = _config.UserName,
                    Password = _config.Password,
                    VirtualHost = _config.VirtualHost,
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
                };

                _connection = factory.CreateConnection();
                _channel = _connection.CreateModel();

                // Declare exchange
                _channel.ExchangeDeclare(
                    exchange: _config.CcgMessagesExchangeName,
                    type: ExchangeType.Direct,
                    durable: true,
                    autoDelete: false
                );

                // Declare queue with TTL and max length
                var queueArgs = new Dictionary<string, object>
                {
                    {"x-message-ttl", _config.MessageTtlHours * 3600 * 1000}, // TTL in milliseconds
                    {"x-max-length", _config.MaxMessagesInQueue},
                    {"x-overflow", "drop-head"} // Drop oldest messages when queue is full
                };

                _channel.QueueDeclare(
                    queue: _config.CcgMessagesQueueName,
                    durable: _config.DurableQueue,
                    exclusive: false,
                    autoDelete: false,
                    arguments: queueArgs
                );

                // Bind queue to exchange
                _channel.QueueBind(
                    queue: _config.CcgMessagesQueueName,
                    exchange: _config.CcgMessagesExchangeName,
                    routingKey: _config.CcgMessagesRoutingKey
                );

                _logger.LogInformation("RabbitMQ initialized successfully. Queue: {Queue}, Exchange: {Exchange}",
                    _config.CcgMessagesQueueName, _config.CcgMessagesExchangeName);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize RabbitMQ connection");
                throw;
            }
        }

        public async Task PublishCcgMessageAsync(StoredCcgMessage message)
        {
            if (_channel == null)
                throw new InvalidOperationException("RabbitMQ channel is not initialized");

            try
            {
                var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var body = Encoding.UTF8.GetBytes(json);

                var properties = _channel.CreateBasicProperties();
                properties.Persistent = _config.PersistentMessages;
                properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                properties.MessageId = message.Id.ToString();
                properties.Headers = new Dictionary<string, object>
                {
                    {"sequence_number", message.SequenceNumber},
                    {"session", message.Session},
                    {"ccg_data_length", message.CcgData.Length}
                };

                _channel.BasicPublish(
                    exchange: _config.CcgMessagesExchangeName,
                    routingKey: _config.CcgMessagesRoutingKey,
                    basicProperties: properties,
                    body: body
                );

                _logger.LogDebug("Published CCG message. Sequence: {Sequence}, Session: {Session}",
                    message.SequenceNumber, message.Session);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish CCG message. Sequence: {Sequence}",
                    message.SequenceNumber);
                throw;
            }
        }

        public async Task<List<StoredCcgMessage>> GetAllCcgMessagesAsync()
        {
            if (_channel == null)
                throw new InvalidOperationException("RabbitMQ channel is not initialized");

            var messages = new List<StoredCcgMessage>();

            try
            {
                var consumer = new EventingBasicConsumer(_channel);
                var messagesReceived = new List<BasicDeliverEventArgs>();
                var completionSource = new TaskCompletionSource<bool>();

                consumer.Received += (model, ea) =>
                {
                    messagesReceived.Add(ea);
                };

                // Start consuming with auto-ack disabled
                var consumerTag = _channel.BasicConsume(
                    queue: _config.CcgMessagesQueueName,
                    autoAck: false,
                    consumer: consumer
                );

                // Wait a short time to collect all available messages
                await Task.Delay(1000);

                // Cancel the consumer
                _channel.BasicCancel(consumerTag);

                // Process collected messages
                foreach (var ea in messagesReceived)
                {
                    try
                    {
                        var json = Encoding.UTF8.GetString(ea.Body.ToArray());
                        var message = JsonSerializer.Deserialize<StoredCcgMessage>(json, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                        });

                        if (message != null)
                        {
                            messages.Add(message);
                        }

                        // Requeue the message (nack with requeue=true)
                        _channel.BasicNack(ea.DeliveryTag, false, true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to deserialize CCG message");
                        // Reject malformed message
                        _channel.BasicNack(ea.DeliveryTag, false, false);
                    }
                }

                // Sort by sequence number
                messages = messages.OrderBy(m => m.SequenceNumber).ToList();

                _logger.LogInformation("Retrieved {Count} CCG messages from queue", messages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve CCG messages from queue");
                throw;
            }

            return messages;
        }

        public async Task<int> GetMessageCountAsync()
        {
            if (_channel == null)
                throw new InvalidOperationException("RabbitMQ channel is not initialized");

            try
            {
                var queueInfo = _channel.QueueDeclarePassive(_config.CcgMessagesQueueName);
                var count = (int)queueInfo.MessageCount;
                
                _logger.LogDebug("Queue {Queue} contains {Count} messages", 
                    _config.CcgMessagesQueueName, count);
                
                return await Task.FromResult(count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get message count from queue");
                return 0;
            }
        }

        public async Task ClearOldMessagesAsync()
        {
            if (_channel == null)
                throw new InvalidOperationException("RabbitMQ channel is not initialized");

            try
            {
                // RabbitMQ will automatically remove old messages based on TTL settings
                // This is more of a maintenance method for manual cleanup if needed
                
                var queueInfo = _channel.QueueDeclarePassive(_config.CcgMessagesQueueName);
                _logger.LogInformation("Queue maintenance check completed. Messages in queue: {Count}",
                    queueInfo.MessageCount);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform queue maintenance");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                _channel?.Close();
                _channel?.Dispose();
                _connection?.Close();
                _connection?.Dispose();
                
                _logger.LogInformation("RabbitMQ connection disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing RabbitMQ connection");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}