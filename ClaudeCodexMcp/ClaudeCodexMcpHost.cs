using ClaudeCodexMcp.Backend;
using ClaudeCodexMcp.Configuration;
using ClaudeCodexMcp.Discovery;
using ClaudeCodexMcp.Logging;
using ClaudeCodexMcp.Notifications;
using ClaudeCodexMcp.Storage;
using ClaudeCodexMcp.Supervisor;
using ClaudeCodexMcp.Tools;
using ClaudeCodexMcp.Usage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ClaudeCodexMcp;

public static class ClaudeCodexMcpHost
{
    public static IHost Create(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        Configure(builder);
        return builder.Build();
    }

    public static void Configure(HostApplicationBuilder builder)
    {
        builder.Configuration
            .AddJsonFile("codex-manager.json", optional: true, reloadOnChange: true)
            .AddJsonFile("codex-manager.local.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("CLAUDE_CODEX_MCP_");

        builder.Services
            .AddOptions<ManagerOptions>()
            .Bind(builder.Configuration.GetSection(ManagerOptions.SectionName));

        ConfigureLogging(builder);
        ConfigureServices(builder);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<CodexTools>();
    }

    private static void ConfigureServices(HostApplicationBuilder builder)
    {
        builder.Services.AddSingleton(serviceProvider =>
        {
            var managerOptions = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ManagerOptions>>().Value;
            return new ManagerStatePaths(managerOptions.ResolveStateDirectory(builder.Environment.ContentRootPath));
        });
        builder.Services.AddSingleton<IProfilePolicyValidator, ProfilePolicyValidator>();
        builder.Services.AddSingleton<JobStore>();
        builder.Services.AddSingleton<QueueStore>();
        builder.Services.AddSingleton<OutputStore>();
        builder.Services.AddSingleton<NotificationStore>();
        builder.Services.AddSingleton<DiscoveryCacheStore>();
        builder.Services.AddSingleton(_ => CodexDiscoveryOptions.FromEnvironment());
        builder.Services.AddSingleton<CodexCapabilityDiscovery>();
        builder.Services.AddSingleton<CodexJobLockRegistry>();
        builder.Services.AddSingleton<UsageReporter>();
        builder.Services.AddSingleton<IClaudeChannelTransport, DisabledClaudeChannelTransport>();
        builder.Services.AddSingleton<ClaudeChannelNotifier>();
        builder.Services.AddSingleton<NotificationDispatcher>();
        builder.Services.AddSingleton<CodexJobSupervisorOptions>();
        builder.Services.AddSingleton<IAppServerJsonRpcClientFactory, CodexAppServerProcessClientFactory>();
        builder.Services.AddSingleton<CodexAppServerBackend>();
        builder.Services.AddSingleton(serviceProvider => new CodexCliBackend(serviceProvider.GetRequiredService<OutputStore>()));
        builder.Services.AddSingleton<ICodexBackendSelector, CodexProfileBackendSelector>();
        builder.Services.AddSingleton<ICodexBackend>(serviceProvider => serviceProvider.GetRequiredService<CodexAppServerBackend>());
        builder.Services.AddSingleton<CodexJobSupervisor>();
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<CodexJobSupervisor>());
        builder.Services.AddSingleton<CodexToolService>();
    }

    private static void ConfigureLogging(HostApplicationBuilder builder)
    {
        var options = builder.Configuration
            .GetSection(ManagerOptions.SectionName)
            .Get<ManagerOptions>() ?? new ManagerOptions();

        var logOptions = options.Logging;
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(logOptions.MinimumLevel);
        builder.Logging.AddConsole(console =>
        {
            console.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Services.Configure<SimpleConsoleFormatterOptions>(formatter =>
        {
            formatter.SingleLine = true;
            formatter.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
            formatter.UseUtcTimestamp = true;
        });

        if (logOptions.EnableFileLogging)
        {
            var logFilePath = options.ResolveLogFilePath(builder.Environment.ContentRootPath);
            builder.Logging.AddProvider(new ManagerFileLoggerProvider(logFilePath, logOptions.MinimumLevel));
        }
    }
}
