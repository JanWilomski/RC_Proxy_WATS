using System.ComponentModel.DataAnnotations;

namespace RC_Proxy_WATS.Configuration
{
    public class ProxyConfiguration
    {
        [Required]
        public string RcServerHost { get; set; } = "127.0.0.1";
        
        [Range(1, 65535)]
        public int RcServerPort { get; set; } = 19083;
        
        [Range(1, 65535)]
        public int ProxyListenPort { get; set; } = 19084;
        
        public string ProxyBindAddress { get; set; } = "0.0.0.0";
        
        public int MaxConcurrentClients { get; set; } = 100;
        
        public int HeartbeatIntervalSeconds { get; set; } = 30;
        
        public int ConnectionTimeoutSeconds { get; set; } = 60;
        
        public bool EnableDebugLogging { get; set; } = false;
    }

    public class RabbitMqConfiguration
    {
        [Required]
        public string HostName { get; set; } = "localhost";
        
        [Range(1, 65535)]
        public int Port { get; set; } = 5672;
        
        public string UserName { get; set; } = "guest";
        
        public string Password { get; set; } = "guest";
        
        public string VirtualHost { get; set; } = "/";
        
        [Required]
        public string CcgMessagesQueueName { get; set; } = "ccg_messages";
        
        [Required]
        public string CcgMessagesExchangeName { get; set; } = "ccg_exchange";
        
        public string CcgMessagesRoutingKey { get; set; } = "ccg.messages";
        
        public bool DurableQueue { get; set; } = true;
        
        public bool PersistentMessages { get; set; } = true;
        
        public int MaxMessagesInQueue { get; set; } = 50000;
        
        public int MessageTtlHours { get; set; } = 24;
    }
}