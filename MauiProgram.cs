﻿using System.Net;
using System.Reflection;
using CommunityToolkit.Maui;
using Discord;
using Discord.WebSocket;
using DiscordFS.Configuration;
using DiscordFS.Storage.Discord;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using Polly;
using Polly.RateLimit;
using Serilog;
using Serilog.Events;

namespace DiscordFS;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        SetupSerilog();

        var builder = MauiApp.CreateBuilder();
        AppBuilderExtensions.UseMauiCommunityToolkit(builder
                .UseMauiApp<App>())
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont(filename: "OpenSans-Regular.ttf", alias: "OpenSansRegular");
                fonts.AddFont(filename: "OpenSans-Semibold.ttf", alias: "OpenSansSemibold");
            })
            .Logging.AddSerilog(Log.Logger);

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.ConfigureLifecycleEvents(lifecycle =>
        {
            lifecycle.AddWindows(windows => windows.OnWindowCreated(window =>
            {
                window.ExtendsContentIntoTitleBar = false;
            }));
        });

        var retryPolicy = GetRetryPolicy();
        var rateLimitPolicy = GetRateLimitPolicy(numberOfExections: 100, TimeSpan.FromSeconds(value: 10));
        var resilienceStrategy = Policy.WrapAsync(retryPolicy, rateLimitPolicy);

        builder.Services.AddHttpClient(name: "DiscordFS")
            .AddPolicyHandler(resilienceStrategy);

        builder.Services.AddSingleton<IDiscordClient, DiscordSocketClient>();
        builder.Services.AddSingleton<IAppConfigurationManager, AppConfigurationManager>();
        ServiceCollectionServiceExtensions.AddSingleton<IDiscordStorageProvider, DiscordStorageProvider>(builder.Services);

        return builder.Build();
    }

    private static IAsyncPolicy<HttpResponseMessage> GetRateLimitPolicy(int numberOfExections, TimeSpan perTimeSpan)
    {
        return Policy.RateLimitAsync<HttpResponseMessage>(numberOfExections, perTimeSpan);
    }

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return Policy<HttpResponseMessage>
            .Handle<HttpRequestException>(x => x.StatusCode == null || (x.StatusCode != null
                                                                        && (int)x.StatusCode is > 500 and <= 599)
                                                                    || x.StatusCode
                                                                        is HttpStatusCode.RequestTimeout
                                                                        or HttpStatusCode.TooManyRequests)
            .Or<RateLimitRejectedException>()
            .WaitAndRetryForeverAsync(
                (retryAttempt, delegateResult, _) =>
                {
                    var retryDelta = delegateResult.Result?.Headers.RetryAfter?.Delta;
                    if (retryDelta != null)
                    {
                        return retryDelta.Value + TimeSpan.FromMilliseconds(value: 100);
                    }

                    if (delegateResult.Exception is RateLimitRejectedException rateLimitRejectedException)
                    {
                        return rateLimitRejectedException.RetryAfter + TimeSpan.FromMilliseconds(value: 100);
                    }

                    if (retryAttempt > 5)
                    {
                        throw delegateResult.Exception ?? new Exception(message: "Retry attempt is greater than 5");
                    }

                    return TimeSpan.FromSeconds(Math.Pow(x: 2, retryAttempt)) // exponential back-off
                           + TimeSpan.FromMilliseconds(Random.Shared.Next(minValue: 0, maxValue: 100)); // jitter
                },
                (_, _, _, _) => Task.CompletedTask);
    }

    private static void SetupSerilog()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!, path2: "logs"));
        if (!dir.Exists)
        {
            dir.Create();
        }

        var logFilePath = Path.Combine(dir.FullName, $"{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.log");

        const string defaultConsoleLogTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}][{SourceContext}] {Message:lj}{NewLine}{Exception}";
        const string defaultFileLogTemplate =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}][{SourceContext}] {Message:lj}{NewLine}{Exception}";

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Async(c => c.Console(LogEventLevel.Information, defaultConsoleLogTemplate))
            .WriteTo.Async(c => c.File(logFilePath, LogEventLevel.Information, defaultFileLogTemplate))
            .CreateLogger();
    }
}