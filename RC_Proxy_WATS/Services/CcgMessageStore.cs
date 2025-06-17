using Microsoft.Extensions.Logging;
using RC_Proxy_WATS.Models;
using System.Collections.Concurrent;

namespace RC_Proxy_WATS.Services
{
    public interface ICcgMessageStore
    {
        Task AddCcgMessageAsync(StoredCcgMessage message);
        Task<List<StoredCcgMessage>> GetAllCcgMessagesAsync();
        Task<List<StoredCcgMessage>> GetCcgMessagesFromSequenceAsync(uint fromSequence);
        Task InitializeFromRabbitMqAsync();
        int MessageCount { get; }
        void ClearCache();
    }

    public class CcgMessageStore : ICcgMessageStore
    {
        private readonly IRabbitMqService _rabbitMqService;
        private readonly ILogger<CcgMessageStore> _logger;
        private readonly ConcurrentDictionary<uint, StoredCcgMessage> _messageCache;
        private readonly ReaderWriterLockSlim _cacheLock;
        private readonly int _maxCacheSize = 10000; // Keep last 10k messages in memory
        private volatile bool _isInitialized = false;

        public int MessageCount => _messageCache.Count;

        public CcgMessageStore(IRabbitMqService rabbitMqService, ILogger<CcgMessageStore> logger)
        {
            _rabbitMqService = rabbitMqService;
            _logger = logger;
            _messageCache = new ConcurrentDictionary<uint, StoredCcgMessage>();
            _cacheLock = new ReaderWriterLockSlim();
        }

        public async Task AddCcgMessageAsync(StoredCcgMessage message)
        {
            try
            {
                // Add to RabbitMQ first for persistence
                await _rabbitMqService.PublishCcgMessageAsync(message);

                // Add to in-memory cache
                _cacheLock.EnterWriteLock();
                try
                {
                    _messageCache.AddOrUpdate(message.SequenceNumber, message, (key, existing) => message);

                    // Maintain cache size limit
                    if (_messageCache.Count > _maxCacheSize)
                    {
                        var oldestMessages = _messageCache.Values
                            .OrderBy(m => m.SequenceNumber)
                            .Take(_messageCache.Count - _maxCacheSize + 100) // Remove extra to avoid frequent cleanup
                            .ToList();

                        foreach (var oldMessage in oldestMessages)
                        {
                            _messageCache.TryRemove(oldMessage.SequenceNumber, out _);
                        }

                        _logger.LogDebug("Cleaned {Count} old messages from cache", oldestMessages.Count);
                    }
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }

                _logger.LogTrace("Added CCG message to store. Sequence: {Sequence}, Cache size: {CacheSize}",
                    message.SequenceNumber, _messageCache.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add CCG message to store. Sequence: {Sequence}",
                    message.SequenceNumber);
                throw;
            }
        }

        public async Task<List<StoredCcgMessage>> GetAllCcgMessagesAsync()
        {
            if (!_isInitialized)
            {
                await InitializeFromRabbitMqAsync();
            }

            _cacheLock.EnterReadLock();
            try
            {
                var messages = _messageCache.Values
                    .OrderBy(m => m.SequenceNumber)
                    .ToList();

                _logger.LogDebug("Retrieved {Count} CCG messages from cache", messages.Count);
                return messages;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        public async Task<List<StoredCcgMessage>> GetCcgMessagesFromSequenceAsync(uint fromSequence)
        {
            if (!_isInitialized)
            {
                await InitializeFromRabbitMqAsync();
            }

            _cacheLock.EnterReadLock();
            try
            {
                var messages = _messageCache.Values
                    .Where(m => m.SequenceNumber > fromSequence)
                    .OrderBy(m => m.SequenceNumber)
                    .ToList();

                _logger.LogDebug("Retrieved {Count} CCG messages from sequence {FromSequence}", 
                    messages.Count, fromSequence);
                return messages;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        public async Task InitializeFromRabbitMqAsync()
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Initializing CCG message store from RabbitMQ...");

                var messages = await _rabbitMqService.GetAllCcgMessagesAsync();

                _cacheLock.EnterWriteLock();
                try
                {
                    _messageCache.Clear();

                    // Keep only the most recent messages in cache
                    var recentMessages = messages
                        .OrderByDescending(m => m.SequenceNumber)
                        .Take(_maxCacheSize)
                        .ToList();

                    foreach (var message in recentMessages)
                    {
                        _messageCache.TryAdd(message.SequenceNumber, message);
                    }

                    _isInitialized = true;
                    
                    _logger.LogInformation("Initialized CCG message store. Total messages in RabbitMQ: {Total}, Cached: {Cached}",
                        messages.Count, _messageCache.Count);
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize CCG message store from RabbitMQ");
                throw;
            }
        }

        public void ClearCache()
        {
            _cacheLock.EnterWriteLock();
            try
            {
                var count = _messageCache.Count;
                _messageCache.Clear();
                _isInitialized = false;
                
                _logger.LogInformation("Cleared CCG message cache. Removed {Count} messages", count);
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        // Helper method to get cache statistics
        public CacheStatistics GetCacheStatistics()
        {
            _cacheLock.EnterReadLock();
            try
            {
                if (_messageCache.IsEmpty)
                {
                    return new CacheStatistics
                    {
                        MessageCount = 0,
                        MinSequenceNumber = null,
                        MaxSequenceNumber = null,
                        IsInitialized = _isInitialized
                    };
                }

                var sequences = _messageCache.Keys.ToList();
                return new CacheStatistics
                {
                    MessageCount = _messageCache.Count,
                    MinSequenceNumber = sequences.Min(),
                    MaxSequenceNumber = sequences.Max(),
                    IsInitialized = _isInitialized
                };
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }
    }

    public class CacheStatistics
    {
        public int MessageCount { get; set; }
        public uint? MinSequenceNumber { get; set; }
        public uint? MaxSequenceNumber { get; set; }
        public bool IsInitialized { get; set; }
    }
}