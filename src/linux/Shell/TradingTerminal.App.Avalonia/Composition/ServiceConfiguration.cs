using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using DaxAlgo.FootprintTransformer;
using DaxAlgo.Daxq.Host;
using TradingTerminal.App.Archive;
using TradingTerminal.App.Avalonia.Shell;
using TradingTerminal.App.Avalonia.Theming;
using TradingTerminal.App.Support;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Ml;
using TradingTerminal.Infrastructure;
using TradingTerminal.Infrastructure.AiAnalyst;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Fast;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Infrastructure.MarketData.Archive;
using TradingTerminal.Infrastructure.MarketData.Archive.Lake;
using TradingTerminal.Infrastructure.MarketData.Archive.Telegram;
using TradingTerminal.Infrastructure.Notifications;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Plugins.Feed;
using TradingTerminal.Infrastructure.Research;
using TradingTerminal.Infrastructure.Regime;
using TradingTerminal.Infrastructure.Sidecar;
using TradingTerminal.Infrastructure.TsdBridge;
using TradingTerminal.Infrastructure.StrategyAgent;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.BacktestStudio;
using TradingTerminal.Backtest.Engine.TradeIr;
using TradingTerminal.LseBacktest;
using TradingTerminal.QuantConnect;
using TradingTerminal.Recording;
using TradingTerminal.Charts;
using TradingTerminal.BubbleChart;
using TradingTerminal.SurfaceLab;
using TradingTerminal.OrderBook;
using TradingTerminal.VolumeFootprint;
using TradingTerminal.Heatmap;
using TradingTerminal.Login;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using TradingTerminal.UI.Strategies;
using TradingTerminal.StrategyComposer;
using TradingTerminal.App.Avalonia.Execution;
using TradingTerminal.App.Plugins;
using TradingTerminal.Execution.Alpaca;
using TradingTerminal.Execution.CTrader;
using TradingTerminal.Execution.InteractiveBrokers;
using TradingTerminal.Execution.Mac;
using TradingTerminal.ExecutionUi;
using TradingTerminal.UI.Execution;

namespace TradingTerminal.App.Avalonia.Composition;

/// <summary>
/// Composition root for the Avalonia shell. Mirrors the Windows Professional Generic Host with
/// cross-platform implementations for configuration, logging, security, and UI services.
/// </summary>
public static class ServiceConfiguration
{
    public static IHost BuildHost(IPluginConsentPrompt? pluginConsentPrompt = null)
    {
        var services = new ServiceCollection();

        // Shipped defaults are layered under environment, local, and per-user UI overrides.
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
            .AddJsonFile(TradingTerminal.App.Notifications.NotificationsUserFile.Path, optional: true, reloadOnChange: true)
            .AddJsonFile(TradingTerminal.App.Archive.ArchiveUserFile.Path, optional: true, reloadOnChange: true)
            .AddJsonFile(TradingTerminal.App.Research.ResearchUserFile.Path, optional: true, reloadOnChange: true)
            .AddJsonFile(TradingTerminal.App.Authoring.AiCodegenUserFile.Path, optional: true, reloadOnChange: true)
            .Build();
        services.AddSingleton(configuration);
        var activityLog = new InMemoryLogSink();
        var configuredMinimum = configuration["Logging:MinimumLevel"] ?? "Information";
        var minimumLevel = Enum.TryParse<LogEventLevel>(configuredMinimum, ignoreCase: true, out var parsedLevel)
            ? parsedLevel
            : LogEventLevel.Information;
        var configuredLogPath = configuration["Logging:FilePath"] ?? "logs/terminal-.log";
        var logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.File(ResolveLogFilePath(configuredLogPath), rollingInterval: RollingInterval.Day)
            .WriteTo.Debug()
            .WriteTo.Sink(new ObservableCollectionLogSink(activityLog))
            .CreateLogger();

        // Universal Activity Log — one shared sink for the whole shell.
        services.AddSingleton(activityLog);

        // Bind the same broker/account/development sections as the Windows Professional shell.
        services.Configure<InteractiveBrokersOptions>(
            configuration.GetSection(InteractiveBrokersOptions.SectionName));
        services.Configure<NinjaTraderOptions>(
            configuration.GetSection(NinjaTraderOptions.SectionName));
        services.Configure<CTraderOptions>(
            configuration.GetSection(CTraderOptions.SectionName));
        services.Configure<AlpacaOptions>(
            configuration.GetSection(AlpacaOptions.SectionName));
        services.PostConfigure<AlpacaOptions>(options =>
        {
            var keyId = options.ApiKey ?? string.Empty;
            var secretKey = options.ApiSecret ?? string.Empty;
            if (!AlpacaCliLocalCredentialBootstrap.TryMergeMissing(ref keyId, ref secretKey, out var profile))
                return;
            options.ApiKey = keyId;
            options.ApiSecret = secretKey;
            activityLog.Append(
                "MarketData",
                "Information",
                $"Alpaca market-data ApiKey/ApiSecret seeded from CLI profile '{profile}'.");
        });
        services.Configure<BinanceOptions>(
            configuration.GetSection(BinanceOptions.SectionName));
        services.Configure<IronBeamOptions>(
            configuration.GetSection(IronBeamOptions.SectionName));
        services.Configure<LondonStrategicEdgeOptions>(
            configuration.GetSection(LondonStrategicEdgeOptions.SectionName));
        services.Configure<UpstoxOptions>(
            configuration.GetSection(UpstoxOptions.SectionName));
        services.Configure<CoinbaseOptions>(
            configuration.GetSection(CoinbaseOptions.SectionName));
        services.Configure<BybitOptions>(
            configuration.GetSection(BybitOptions.SectionName));
        services.Configure<KrakenOptions>(
            configuration.GetSection(KrakenOptions.SectionName));
        services.Configure<OkxOptions>(
            configuration.GetSection(OkxOptions.SectionName));
        services.Configure<DevOptions>(configuration.GetSection(DevOptions.SectionName));
        services.Configure<GoogleAuthOptions>(configuration.GetSection(GoogleAuthOptions.SectionName));
        services.Configure<SimulatedBrokerOptions>(
            configuration.GetSection(SimulatedBrokerOptions.SectionName));

        // Headless pipeline + broker layer (WPF-free on net9.0) and the backtest strategy catalog.
        services.AddTradingTerminalInfrastructure();
        services.AddMarketDataPipeline(configuration);
        services.AddSingleton<IFootprintForecastProvider, FootprintTransformerForecastProvider>();
        // Marshal repository/Paper-Lab UI work onto Avalonia's UI thread (overrides the headless
        // ImmediateUiDispatcher default registered by the pipeline; last registration wins).
        services.AddSingleton<TradingTerminal.Infrastructure.Threading.IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddNotifications(configuration);
        services.AddMarketRegime(configuration);
        // AI analyst seam (IAiAnalystClient Null/Http, hot-swappable via NotificationsOptions).
        services.AddAiAnalyst(configuration);
        // Paper Lab research/repro seams (IPaperIngestClient/IReproOrchestrator Null defaults).
        services.AddPaperResearch(configuration);
        services.AddSidecar(configuration);
        services.AddTsdBridge(configuration);
        // Dedicated QueryEngine -> VibeQuant/AKQuant + CSP backend. It remains disabled by default;
        // the Strategy Builder can inspect/start an already confirmed retained run when configured.
        services.AddStrategyAgent(configuration);
        // Market-data archive (offloader + manifest store + Telegram transport), with native
        // Avalonia prompting and macOS-protected credential post-configuration layered on top.
        services.AddMarketDataArchive(configuration);
        services.AddSingleton<ITelegramAuthPrompt, AvaloniaTelegramAuthPrompt>();
        services.AddSingleton<ITelegramArchiveLogin, TelegramArchiveLogin>();
        services.AddSingleton<IPostConfigureOptions<TelegramArchiveOptions>, TelegramArchiveOptionsPostConfigure>();
        services.AddParquetLake(configuration);
        services.AddBacktestStrategyCatalog();
        services.AddSingleton<TradingTerminal.UI.Strategies.IVisualizerRegistry,
            TradingTerminal.UI.Strategies.VisualizerRegistry>();
        // StrategyKernelRegistry is constructed after PluginLoader so authored plugin
        // registrations and durable open-packages can both populate the catalog.
        services.AddFastBacktestRunner();

        var useLocalDaxqLicensing = false;
#if DEBUG
        useLocalDaxqLicensing =
            string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase) ||
            environmentName.StartsWith("Dev", StringComparison.OrdinalIgnoreCase);
#endif
        var protectedStrategyEngine = new DaxqProtectedStrategyEngine(
            NullLogger<DaxqProtectedStrategyEngine>.Instance,
            new DaxqProtectedStrategyEngineOptions
            {
                EnableLocalDevelopmentProtocol = useLocalDaxqLicensing,
                ForceReferenceVm = useLocalDaxqLicensing,
            });
        services.AddSingleton(protectedStrategyEngine);
        services.AddSingleton<TradingTerminal.Infrastructure.Plugins.IProtectedStrategyEngine>(
            protectedStrategyEngine);
        services.AddSingleton<DaxqStrategyInstaller>();

        // Strategy plug-in seam — the SAME factory the WPF shell uses. Every strategy resolves and
        // opens through IStrategyFactory.Create(id); the shell never names a concrete strategy. Each
        // strategy project registers a StrategyFactoryRegistration (Avalonia view) on its net9.0 leg.
        services.AddSingleton<IStrategyFactory, StrategyFactory>();
        services.TryAddSingleton<TradingTerminal.Core.Strategies.Authoring.IStrategyCompiler,
            RoslynStrategyCompiler>();
        services.TryAddSingleton<TradingTerminal.Core.Strategies.Generation.IAuthoredUnitCompilerV1,
            RoslynAuthoredUnitCompilerV1>();
        services.AddStrategyCodegen(configuration);
        services.AddSingleton<TradingTerminal.Core.Strategies.Authoring.ITradeIrSimulatedBacktestRunnerV1,
            TradeIrSimulatedBacktestRunnerV1>();
        services.AddSingleton<TradingTerminal.Core.Strategies.Generation.IResearchExperimentRunnerV1,
            StrategyResearchExperimentRunnerV1>();
        services.AddHttpClient(nameof(ResearchConditionSearchV1), (sp, http) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<TradingTerminal.Core.Configuration.TsdBridgeOptions>>()
                .CurrentValue;
            var baseUrl = string.IsNullOrWhiteSpace(opts.BaseUrl) ? "http://127.0.0.1:8000" : opts.BaseUrl.TrimEnd('/');
            http.BaseAddress = new Uri(baseUrl + "/");
            http.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddSingleton<TradingTerminal.Core.Strategies.Generation.IResearchConditionSearchV1,
            ResearchConditionSearchV1>();

        var pluginsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgoTerminal", "plugins");
        var pluginOptions = configuration.GetSection(PluginsOptions.SectionName).Get<PluginsOptions>()
            ?? new PluginsOptions();
        var pluginPolicy = PluginTrustPolicy.From(pluginOptions);
        var disableStrategyPlugins = configuration
            .GetSection(DevOptions.SectionName)
            .GetValue<bool>(nameof(DevOptions.DisableStrategyPlugins));
        if (disableStrategyPlugins)
        {
            services.AddSingleton(new PluginHostContext(pluginsRoot, pluginPolicy, []));
        }
        else
        {
            var pluginState = new PluginStateStore(pluginsRoot);
            var pluginReport = PluginLoader.LoadWithReport(
                services,
                pluginsRoot,
                DaxAlgo.Sdk.SdkInfo.Version,
                pluginPolicy,
                pluginState,
                pluginOptions.ScanMode,
                consent: pluginConsentPrompt,
                protectedStrategyEngine: protectedStrategyEngine);
            services.AddSingleton(new PluginHostContext(
                pluginsRoot, pluginPolicy, pluginReport.Loaded, pluginReport, pluginState));
        }
        services.AddPluginFeed(pluginOptions);
        services.AddSingleton<AuthoredStrategyInstaller>();
        services.AddSingleton<TradingTerminal.UI.Strategies.IStrategyKernelRegistry>(sp =>
        {
            var registry = new TradingTerminal.UI.Strategies.StrategyKernelRegistry(
                sp.GetServices<DaxAlgo.Sdk.AuthoredStrategyKernelPluginRegistration>());
            try
            {
                OpenPackageHostRegistrar.RegisterInstalled(
                    pluginsRoot,
                    registry,
                    sp.GetRequiredService<TradingTerminal.Core.Strategies.Generation.IAuthoredUnitCompilerV1>(),
                    sp.GetRequiredService<TradingTerminal.UI.Strategies.IVisualizerRegistry>());
            }
            catch
            {
                // Durable open-package catalog is best-effort at startup; install path registers live.
            }
            return registry;
        });
        services.AddTransient<TradingTerminal.App.Plugins.PluginManagerViewModel>();
        services.AddTransient<TradingTerminal.App.Plugins.PluginManagerView>();

        // Shared live-strategy plumbing (same bundle the WPF shell injects into every per-strategy VM).
        services.AddSingleton<ISignalGeneratorRouterFactory, SignalGeneratorRouterFactory>();
        services.AddSingleton(sp => new LiveStrategyHostServices(
            sp.GetRequiredService<IMarketDataRepository>(),
            sp.GetRequiredService<IMarketDataHub>(),
            sp.GetRequiredService<IMarketDataIngest>(),
            sp.GetRequiredService<IMarketDataStore>(),
            sp.GetRequiredService<IBrokerSelector>(),
            sp.GetRequiredService<InMemoryLogSink>(),
            sp.GetRequiredService<IInstrumentRegistry>()));

        // Ported per-strategy VMs (descriptor + portable VM; the WPF windows are #if'd out on net9.0).
        // Index Regime Graph consumes the Advanced Market Regime engine (Infrastructure, net9.0).
        services.TryAddSingleton<TradingTerminal.Core.MarketData.AdvancedRegime.IAdvancedRegimeProvider,
            TradingTerminal.Infrastructure.Regime.AdvancedRegime.AdvancedRegimeService>();

        // Shell view-models. The header API meter polls IBrokerApiMeter (registered by the
        // Infrastructure layer); MainWindowViewModel binds the catalog to IStrategyFactory.All.
        services.AddSingleton<BrokerApiMeterViewModel>();
        services.AddSingleton<IThemeManager, ThemeManager>();

        // Persistent Paper books own lazy, account-isolated sessions. The shell acquires the
        // explicitly selected book for each Console/Runner window.
        services.AddSingleton<IPaperExecutionBookStore, JsonPaperExecutionBookStore>();
        services.AddSingleton<PaperExecutionBookManager>();
        services.AddSingleton<PaperExecutionBooksViewModel>();
        services.AddTransient<PaperExecutionBooksWindow>();
        services.AddTransient<PaperExecutionConsoleWindow>();
        services.AddTransient<PaperStrategyRunnerWindow>();

        // Live-capable Execution Console (Windows parity). Every venue is an independent opt-in
        // section, Paper is the shipped mode, and LIVE additionally needs that venue's own owner
        // flag, real credentials, and a typed confirmation persisted in the login Keychain.
        // The dialog is registered before AddExecutionConsole because that call TryAdds the
        // fail-closed "refuse every prompt" default for hosts with no UI head.
        var liveConfirmations = new MacKeychainLiveExecutionConfirmationStore();
        services.AddSingleton<TradingTerminal.Execution.Oms.ILiveExecutionConfirmationStore>(liveConfirmations);
        services.AddSingleton<IExecutionConfirmationService, AvaloniaExecutionConfirmationService>();
        AddGatedVenueExecution(services, configuration, liveConfirmations, activityLog);
        services.AddSingleton<IExecutionFillSink, TsdExecutionFillSink>();
        services.AddExecutionConsole();
        services.AddSingleton<IExecutionFillReplay>(sp =>
            (InProcessExecutionClient)sp.GetRequiredService<IExecutionClient>());
        services.AddTransient<ExecutionConsoleWindow>();

        // AI tool VMs (portable — ILogger-only ctors; file I/O via the UiFile seam).
        services.AddTransient<TradingTerminal.Ai.MarketAnalyst.AiAnalystViewModel>();
        services.AddTransient<TradingTerminal.Ai.PaperLab.PaperLabViewModel>();

        // Settings/aux VMs (extracted to the shared TradingTerminal.Settings project — portable).
        services.AddTransient<TradingTerminal.App.Notifications.NotificationsSettingsViewModel>();
        services.AddTransient<TradingTerminal.App.Research.ResearchSettingsViewModel>();
        services.AddTransient<TradingTerminal.App.Support.SupportViewModel>();
        services.AddTransient<TradingTerminal.App.Avalonia.Settings.SupportWindow>();
        services.AddSingleton<ISupportPrompt, SupportPrompt>();
        services.AddTransient<TradingTerminal.App.Authoring.StrategyAuthoringViewModel>();
        services.AddTransient<TradingTerminal.App.Authoring.AiProvidersSettingsViewModel>();
        // Roslyn strategy compiler backs the authoring window.
        services.AddTransient<TradingTerminal.App.Archive.ArchiveSettingsViewModel>();
        services.AddTransient<TradingTerminal.App.Archive.ArchiveActivityViewModel>();
        services.AddTransient<TradingTerminal.Ai.FactorResearch.FactorResearchViewModel>();
        services.AddTransient<TradingTerminal.Ai.MlFeatures.MlFeaturesViewModel>();
        services.AddTransient<TradingTerminal.Ai.BacktestAnalysis.BacktestAnalysisViewModel>();
        services.AddTransient<TradingTerminal.AdvancedMarketRegime.AdvancedMarketRegimeViewModel>();
        services.AddBacktestStudioSurface();
        services.AddRecordingSurface();
        services.AddLseBacktestSurface();
        services.AddTransient<TradingTerminal.Correlation.LiveCorrelationMatrixViewModel>();
        services.AddQuantConnectSurface(configuration);
        services.AddOrderBookSurface();
        services.AddFootprintSurface();
        services.AddHeatmapSurface();
        services.AddChartsSurface();
        services.AddBubbleChartSurface();
        services.AddSurfaceLabSurface();
        services.AddStrategyViewComposer();
        TradingTerminal.Backtest.BacktestServiceCollectionExtensions.AddBacktestSurface(services);
        services.AddLogin();
        services.AddCredentialedLoginForms();

        services.AddSingleton<MainWindowViewModel>();

        return new HostBuilder()
            .UseSerilog(logger, dispose: true)
            .ConfigureServices(hostServices =>
            {
                foreach (var descriptor in services)
                    hostServices.Add(descriptor);
            })
            .Build();
    }

    /// <summary>
    /// Registers each execution venue from its own configuration section. A section that is absent,
    /// disabled, or refused by its endpoint gate leaves that venue unregistered — the console then
    /// shows it as unavailable instead of the shell failing to start. Binance is deliberately not
    /// here: it stays market-data only.
    /// </summary>
    private static void AddGatedVenueExecution(
        IServiceCollection services,
        IConfiguration configuration,
        TradingTerminal.Execution.Oms.ILiveExecutionConfirmationStore confirmations,
        InMemoryLogSink activityLog)
    {
        void Register(string venue, Action register)
        {
            try
            {
                register();
            }
            catch (Exception exception)
            {
                activityLog.Append(
                    "Execution",
                    "Warning",
                    $"{venue} execution stayed unregistered and can route no orders: {exception.Message}");
            }
        }

        Register("Alpaca", () => services.AddAlpacaExecution(
            options =>
            {
                configuration.GetSection(AlpacaExecutionOptions.SectionName).Bind(options);
                if (AlpacaCliLocalCredentialBootstrap.TryApplyMissing(options, out var profile))
                {
                    activityLog.Append(
                        "Execution",
                        "Information",
                        $"Alpaca execution KeyId/SecretKey seeded from CLI profile '{profile}' " +
                        "(~/.config/alpaca). Brokers-form credentials still override at Connect.");
                }
            },
            confirmationStore: confirmations));
        Register("cTrader", () => services.AddCTraderExecution(
            options => configuration.GetSection(CTraderExecutionOptions.SectionName).Bind(options),
            confirmationStore: confirmations));

        // The IB adapter constructs its native TWS transport eagerly the first time it is resolved,
        // so registering it without CSharpAPI.dll would take the whole console down at open time
        // rather than showing one unavailable broker card.
        var interactiveBrokers = configuration.GetSection(InteractiveBrokersExecutionOptions.SectionName);
        if (interactiveBrokers.GetValue<bool>(nameof(InteractiveBrokersExecutionOptions.Enabled)) &&
            Type.GetType("IBApi.EClientSocket, CSharpAPI") is null)
        {
            activityLog.Append(
                "Execution",
                "Warning",
                "Interactive Brokers execution is enabled but the TWS API (CSharpAPI.dll) was not " +
                "resolved at build time. Install the TWS API or set TwsApiClientDll, then rebuild.");
        }
        else
        {
            Register("Interactive Brokers", () => services.AddInteractiveBrokersExecution(
                options => interactiveBrokers.Bind(options),
                confirmationStore: confirmations));
        }
    }

    private static string ResolveLogFilePath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
            return configuredPath;

        var userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(userData))
            userData = Path.Combine(Path.GetTempPath(), "DaxAlgoTerminal");
        else
            userData = Path.Combine(userData, "DaxAlgoTerminal");

        var relative = configuredPath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(userData, relative);
    }
}
