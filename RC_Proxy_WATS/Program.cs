using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RC_Proxy_WATS.Services;
using RC_Proxy_WATS.Configuration;

namespace RC_Proxy_WATS
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("RC_Proxy_WATS - Starting proxy server...");

            var host = CreateHostBuilder(args).Build();

            try
            {
                await host.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Application terminated unexpectedly: {ex.Message}");
            }
        }

        static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    // Configuration
                    services.Configure<ProxyConfiguration>(
                        context.Configuration.GetSection("ProxyConfiguration"));
                    services.Configure<RabbitMqConfiguration>(
                        context.Configuration.GetSection("RabbitMQ"));

                    // Services
                    services.AddSingleton<IRabbitMqService, RabbitMqService>();
                    services.AddSingleton<ICcgMessageStore, CcgMessageStore>();
                    services.AddSingleton<IRcConnectionService, RcConnectionService>();
                    services.AddSingleton<IClientConnectionManager, ClientConnectionManager>();
                    services.AddHostedService<ProxyService>();
                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                    logging.SetMinimumLevel(LogLevel.Information);
                });
    }
}