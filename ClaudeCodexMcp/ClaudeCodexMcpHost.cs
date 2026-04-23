using ClaudeCodexMcp.Configuration;
using ClaudeCodexMcp.Logging;
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

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport();
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
