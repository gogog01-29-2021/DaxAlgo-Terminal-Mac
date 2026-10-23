using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Infrastructure.TsdBridge;

public static class TsdBridgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the silent TSD sidecar bridge. Safe when TSD is offline (SoftFail).
    /// </summary>
    public static IServiceCollection AddTsdBridge(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TsdBridgeOptions>(configuration.GetSection(TsdBridgeOptions.SectionName));
        services.AddSingleton<TsdPendingConfirmStore>();
        services.AddSingleton<ITsdPendingConfirmStore>(sp => sp.GetRequiredService<TsdPendingConfirmStore>());
        services.AddHttpClient<TsdBridgeClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<TsdBridgeOptions>>()
                .CurrentValue;
            var baseUrl = string.IsNullOrWhiteSpace(opts.BaseUrl)
                ? "http://127.0.0.1:8000"
                : opts.BaseUrl.TrimEnd('/') + "/";
            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddSingleton<ITsdFillPoster, TsdBridgeFillPoster>();
        services.AddSingleton<TsdFillMirror>();
        services.AddHostedService<TsdBridgeHostedService>();
        return services;
    }
}
