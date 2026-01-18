using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// RetryConfig controls retry behavior for transient failures.
/// </summary>
public record RetryConfig
{
    public int MaxRetries { get; init; }
    public TimeSpan InitialBackoff { get; init; }
    public TimeSpan MaxBackoff { get; init; }
    public double Multiplier { get; init; }

    public static RetryConfig Default() => new()
    {
        MaxRetries = 3,
        InitialBackoff = TimeSpan.FromMilliseconds(500),
        MaxBackoff = TimeSpan.FromSeconds(8),
        Multiplier = 2.0
    };
}

public class RetryableApiError : Exception
{
    public int StatusCode { get; }
    public bool IsRetryable { get; }

    public RetryableApiError(string message, int statusCode, bool isRetryable, Exception? innerException = null)
        : base(statusCode > 0 ? $"API error (status {statusCode}): {message}" : $"API error: {message}", innerException)
    {
        StatusCode = statusCode;
        IsRetryable = isRetryable;
    }
}

public static class RetryHelper
{
    public static bool IsRetryableError(Exception? error)
    {
        if (error == null)
            return false;

        return error switch
        {
            SocketException => true,
            HttpRequestException httpEx => httpEx.InnerException is SocketException,
            TaskCanceledException => false,
            _ => false
        };
    }

    public static bool IsRetryableStatusCode(int statusCode)
    {
        return statusCode >= 500 || statusCode == 429;
    }

    public static async Task<T> ExecuteWithRetry<T>(
        RetryConfig? config,
        Func<Task<T>> fn,
        CancellationToken cancellationToken = default)
    {
        if (config == null || config.MaxRetries <= 0)
        {
            return await fn();
        }

        Exception? lastError = null;
        var backoff = config.InitialBackoff;

        for (int attempt = 0; attempt <= config.MaxRetries; attempt++)
        {
            try
            {
                return await fn();
            }
            catch (Exception ex)
            {
                lastError = ex;

                if (ex is RetryableApiError retryErr && !retryErr.IsRetryable)
                {
                    throw;
                }

                if (!IsRetryableError(ex))
                {
                    throw;
                }

                if (attempt >= config.MaxRetries)
                {
                    break;
                }

                await Task.Delay(backoff, cancellationToken);

                backoff = TimeSpan.FromMilliseconds(backoff.TotalMilliseconds * config.Multiplier);
                if (backoff > config.MaxBackoff)
                {
                    backoff = config.MaxBackoff;
                }
            }
        }

        throw new Exception($"Retry exhausted after {config.MaxRetries + 1} attempts", lastError);
    }

    public static async Task ExecuteWithRetry(
        RetryConfig? config,
        Func<Task> fn,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetry(config, async () =>
        {
            await fn();
            return 0;
        }, cancellationToken);
    }
}
