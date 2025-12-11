using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PRN232_GradingSystem_Worker.Configuration;
using PRN232_GradingSystem_Worker_Services.Implementations;
using PRN232_GradingSystem_Worker_Services.Interfaces;
using Microsoft.Extensions.Logging;
using PRN232_GradingSystem_Worker_Repo.DBContext;
using Microsoft.Playwright;
using PRN232_GradingSystem_Worker_Services.Settings;

namespace PRN232_GradingSystem_Worker.Extensions;

public static class ServiceCollectionExtensions
{
    /// Register RabbitMQ configuration
    public static IServiceCollection AddRabbitMQConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var config = configuration.GetSection(RabbitMQConfiguration.SectionName).Get<RabbitMQConfiguration>() 
            ?? new RabbitMQConfiguration();
        services.AddSingleton(config);
        return services;
    }

    /// Register Backblaze configuration
    public static IServiceCollection AddBackblazeConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var config = configuration.GetSection(BackblazeConfiguration.SectionName).Get<BackblazeConfiguration>() 
            ?? new BackblazeConfiguration();
        services.AddSingleton(config);
        return services;
    }
    /// Register grading configuration
    public static IServiceCollection AddGradingConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var config = configuration.GetSection(GradingConfiguration.SectionName).Get<GradingConfiguration>() 
            ?? new GradingConfiguration();
        services.AddSingleton(config);
        return services;
    }

    /// Register callback API configuration
    public static IServiceCollection AddCallbackApiConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var config = configuration.GetSection(CallbackApiConfiguration.SectionName).Get<CallbackApiConfiguration>() 
            ?? new CallbackApiConfiguration();
        services.AddSingleton(config);
        return services;
    }

    /// Register database reset service
    public static IServiceCollection AddDatabaseServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var sqlConn = configuration.GetConnectionString("DefaultConnection");
        services.AddSingleton<IDbResetService>(sp => 
            new SqlServerDbResetService(sqlConn!));
        return services;
    }

    /// Register file download services (Backblaze)
    public static IServiceCollection AddFileDownloadServices(
        this IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddHttpClient<IFileDownloadService>(
            client => client.Timeout = TimeSpan.FromMinutes(10));
        
        services.AddSingleton<IFileDownloadService>(sp =>
        {
            var httpClient = sp.GetRequiredService<HttpClient>();
            var config = sp.GetRequiredService<BackblazeConfiguration>();
            var backblazeConfig = new BackblazeConfig
            {
                Mode = config.Mode,
                KeyId = config.KeyId,
                ApplicationKey = config.ApplicationKey,
                BucketName = config.BucketName,
                ApiEndpoint = config.ApiEndpoint,
                S3Endpoint = config.S3Endpoint
            };
            return new BackblazeNativeDownloadService(httpClient, backblazeConfig);
        });
        
        return services;
    }

    /// Register callback service
    public static IServiceCollection AddCallbackService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var config = configuration.GetSection(CallbackApiConfiguration.SectionName).Get<CallbackApiConfiguration>() 
            ?? new CallbackApiConfiguration();
        
        services.AddSingleton<ICallbackService>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            return new HttpCallbackService(
                config.BaseUrl,
                config.UpdateResultEndpoint,
                config.TimeoutSeconds,
                httpClientFactory);
        });

        return services;
    }

    /// Register process tracker service (singleton)
    public static IServiceCollection AddProcessTrackerService(this IServiceCollection services)
    {
        services.AddSingleton<ProcessTrackerService>();
        return services;
    }

    /// Register grading pipeline service
    public static IServiceCollection AddGradingPipeline(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var workingDir = Path.Combine(Path.GetTempPath(), "GradingWorker");
        Directory.CreateDirectory(workingDir);

        var resetScriptPath = Path.Combine(AppContext.BaseDirectory, "Resources", "ResetDb.sql");
        var resetScript = File.Exists(resetScriptPath)
            ? File.ReadAllText(resetScriptPath)
            : "";

        services.AddSingleton<IGradingPipeline>(sp =>
        {
            var fileDownloadService = sp.GetRequiredService<IFileDownloadService>();
            var dbResetService = sp.GetRequiredService<IDbResetService>();
            var serviceScopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var processTracker = sp.GetRequiredService<ProcessTrackerService>();
            var logger = sp.GetRequiredService<ILogger<PRN232_GradingSystem_Worker_Services.Implementations.GradingPipeline>>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            return new PRN232_GradingSystem_Worker_Services.Implementations.GradingPipeline(
                fileDownloadService,
                dbResetService,
                serviceScopeFactory,
                processTracker,
                logger,
                configuration,
                workingDir,
                resetScript);
        });

        return services;
    }

    /// Register duplicate detection services
    public static IServiceCollection AddDuplicateDetectionServices(
        this IServiceCollection services)
    {
        // Register helper services
        services.AddSingleton<FingerprintService>();
        services.AddSingleton<SignatureService>();
        services.AddSingleton<CodeParserService>();

        // Register main service
        services.AddScoped<IDuplicateDetectionService>(sp =>
        {
            var context = sp.GetRequiredService<PRN232_Grading_System_GradingContext>();
            var fingerprintService = sp.GetRequiredService<FingerprintService>();
            var signatureService = sp.GetRequiredService<SignatureService>();
            var codeParserService = sp.GetRequiredService<CodeParserService>();
            var logger = sp.GetRequiredService<ILogger<DuplicateDetectionService>>();
            
            return new DuplicateDetectionService(context, fingerprintService, signatureService, codeParserService, logger);
        });

        return services;
    }

    /// Register UI testing service (Playwright)
    public static IServiceCollection AddUiTestingServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<PlaywrightTestSettings>(
            configuration.GetSection(PlaywrightTestSettings.SectionName));
        services.AddScoped<IUITestService, PlaywrightTestService>();
        return services;
    }
}

