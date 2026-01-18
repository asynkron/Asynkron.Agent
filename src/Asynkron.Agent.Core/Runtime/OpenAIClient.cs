using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Asynkron.Agent.Core.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// OpenAIClient wraps the HTTP client required to call the OpenAI Responses API.
/// </summary>
public sealed class OpenAIClient
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _reasoningEffort;
    private readonly HttpClient _httpClient;
    private readonly PlanSchema.ToolDefinition _tool;
    private readonly string _baseUrl;
    private readonly ILogger _logger;
    private readonly RetryConfig? _retryConfig;

    private const string DefaultOpenAIBaseUrl = "https://api.openai.com/v1";

    public OpenAIClient(
        string apiKey,
        string model,
        string reasoningEffort,
        string baseUrl,
        ILogger logger,
        RetryConfig? retryConfig,
        TimeSpan httpTimeout)
    {
        if (string.IsNullOrEmpty(apiKey))
            throw new ArgumentException("openai: API key is required", nameof(apiKey));

        if (string.IsNullOrEmpty(model))
            throw new ArgumentException("openai: model is required", nameof(model));

        baseUrl = (baseUrl ?? "").Trim();
        if (string.IsNullOrEmpty(baseUrl))
            baseUrl = DefaultOpenAIBaseUrl;

        var tool = PlanSchema.GetDefinition();

        _apiKey = apiKey;
        _model = model;
        _reasoningEffort = reasoningEffort;
        _httpClient = new HttpClient { Timeout = httpTimeout };
        _tool = tool;
        _baseUrl = baseUrl;
        _logger = logger ?? NullLogger.Instance;
        _retryConfig = retryConfig;
    }

    /// <summary>
    /// RequestPlan sends the accumulated chat history to OpenAI and returns the
    /// resulting tool call payload so the runtime can perform validation before
    /// decoding it.
    /// </summary>
    public Task<ToolCall> RequestPlanAsync(CancellationToken ctx, List<ChatMessage> history)
    {
        // Non-streaming path reuses the Responses API implementation without emitting deltas.
        return RequestPlanStreamingResponsesAsync(ctx, history, null);
    }

    /// <summary>
    /// RequestPlanStreamingResponses streams using the modern OpenAI Responses API.
    /// It maps response.output_text.delta chunks to the onDelta callback and collects
    /// function_call deltas into a ToolCall to return on completion.
    /// </summary>
    public async Task<ToolCall> RequestPlanStreamingResponsesAsync(
        CancellationToken ctx,
        List<ChatMessage> history,
        Action<string>? onDelta)
    {
        var start = DateTime.UtcNow;
        _logger.LogDebug("Requesting plan from OpenAI. Model={Model} HistoryLength={HistoryLength}", _model, history.Count);

        // Optional debug streaming: set GOAGENT_DEBUG_STREAM=1 to enable verbose prints
        var debugStream = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOAGENT_DEBUG_STREAM"));
        if (debugStream)
        {
            Console.WriteLine("====== STREAM: entering RequestPlanStreamingResponses");
        }

        // Build request
        var inputMsgs = BuildMessagesFromHistory(history);
        var payload = BuildRequestBody(inputMsgs);

        // Execute request with retry logic
        var resp = await ExecuteRequestAsync(ctx, payload, start, _retryConfig);
        
        try
        {
            // Parse stream
            using var reader = new StreamReader(resp.Content.ReadAsStream());
            var parser = new OpenAIStreamParser(reader, onDelta, debugStream);
            var toolCall = await parser.ParseAsync();

            var duration = DateTime.UtcNow - start;

            if (!string.IsNullOrEmpty(toolCall.Name))
            {
                _logger.LogDebug("OpenAI API request completed successfully. DurationMs={DurationMs} ToolName={ToolName}",
                    duration.TotalMilliseconds,
                    toolCall.Name);
            }
            else
            {
                _logger.LogDebug("OpenAI API request completed (no tool call). DurationMs={DurationMs}", duration.TotalMilliseconds);
            }

            return toolCall;
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - start;
            _logger.LogError(ex, "OpenAI API stream parsing failed. DurationMs={DurationMs} Model={Model}",
                duration.TotalMilliseconds,
                _model);
            throw new Exception($"openai: stream parsing failed: {ex.Message}", ex);
        }
        finally
        {
            resp.Dispose();
        }
    }

    private static List<Dictionary<string, object>> BuildMessagesFromHistory(List<ChatMessage> history)
    {
        var inputMsgs = new List<Dictionary<string, object>>(history.Count);
        foreach (var m in history)
        {
            // Map tool role to developer for Responses API
            var finalRole = m.Role.ToString().ToLowerInvariant();
            if (m.Role == MessageRole.Tool)
            {
                finalRole = "developer";
            }

            // Determine content type expected by the Responses API for final role
            var contentType = "input_text";
            if (finalRole == "assistant")
            {
                contentType = "output_text";
            }

            var msg = new Dictionary<string, object>
            {
                ["role"] = finalRole,
                ["content"] = new List<Dictionary<string, object>>
                {
                    new()
                    {
                        ["type"] = contentType,
                        ["text"] = m.Content
                    }
                }
            };

            inputMsgs.Add(msg);
        }
        return inputMsgs;
    }

    private byte[] BuildRequestBody(List<Dictionary<string, object>> inputMsgs)
    {
        var reqBody = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["input"] = inputMsgs,
            ["stream"] = true,
            // Define the function tool in the flat Responses shape and require a tool call.
            ["tools"] = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["type"] = "function",
                    ["name"] = _tool.Name,
                    ["description"] = _tool.Description,
                    ["parameters"] = _tool.Parameters
                }
            },
            // Require a tool call; with only one tool defined, this forces the model
            // to call our tool with arguments.
            ["tool_choice"] = "required"
        };

        if (!string.IsNullOrEmpty(_reasoningEffort))
        {
            reqBody["reasoning"] = new Dictionary<string, object>
            {
                ["effort"] = _reasoningEffort
            };
        }

        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(reqBody);
    }

    private async Task<HttpResponseMessage> ExecuteRequestAsync(
        CancellationToken ctx,
        byte[] payload,
        DateTime start,
        RetryConfig? retryConfig)
    {
        HttpResponseMessage? resp = null;
        Exception? lastErr = null;

        await RetryHelper.ExecuteWithRetry(_retryConfig, async () =>
        {
            // Create new request for each retry attempt
            var apiRoot = _baseUrl.TrimEnd('/');
            var url = $"{apiRoot}/responses";

            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(payload)
            };
            req.Headers.Add("Authorization", $"Bearer {_apiKey}");
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            try
            {
                resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx);

                if (!resp.IsSuccessStatusCode)
                {
                    var msg = await resp.Content.ReadAsStringAsync(ctx);
                    if (msg.Length > 4096)
                        msg = msg[..4096];
                    
                    resp.Dispose();
                    var duration = DateTime.UtcNow - start;
                    var retryable = RetryHelper.IsRetryableStatusCode((int)resp.StatusCode);

                    var statusError = new RetryableApiError(
                        $"openai(responses): status {resp.StatusCode}: {msg}",
                        (int)resp.StatusCode,
                        retryable);
                    _logger.LogError(statusError, "OpenAI API returned error status. StatusCode={StatusCode} DurationMs={DurationMs} Retryable={Retryable}",
                        (int)resp.StatusCode,
                        duration.TotalMilliseconds,
                        retryable);
                    lastErr = statusError;
                    resp = null;
                    throw lastErr;
                }
            }
            catch (HttpRequestException ex)
            {
                var duration = DateTime.UtcNow - start;
                var retryable = RetryHelper.IsRetryableError(ex);
                _logger.LogError(ex, "OpenAI API request failed. Url={Url} DurationMs={DurationMs} Retryable={Retryable}",
                    url,
                    duration.TotalMilliseconds,
                    retryable);

                lastErr = new RetryableApiError(
                    $"openai(responses): do request: {ex.Message}",
                    0,
                    retryable,
                    ex);
                throw lastErr;
            }
        }, ctx);

        if (resp == null)
        {
            var duration = DateTime.UtcNow - start;
            if (lastErr != null)
                throw lastErr;
            throw new Exception("openai: request failed");
        }

        return resp;
    }

    /// <summary>
    /// extractPartialJSONStringField scans a partial JSON object for a given field name
    /// and returns the raw (still JSON-escaped) string value content if found.
    /// complete=true when an unescaped closing quote was found.
    /// </summary>
    internal static (string raw, bool complete, bool ok) ExtractPartialJSONStringField(string buf, string field)
    {
        // Find the last occurrence to favor the most recent partial chunk.
        var key = $"\"{field}\"";
        var idx = buf.LastIndexOf(key, StringComparison.Ordinal);
        if (idx == -1)
        {
            return ("", false, false);
        }

        // Scan forward: key" : "
        var i = idx + key.Length;
        // skip whitespace
        while (i < buf.Length && (buf[i] == ' ' || buf[i] == '\n' || buf[i] == '\t' || buf[i] == '\r'))
        {
            i++;
        }
        if (i >= buf.Length || buf[i] != ':')
        {
            return ("", false, false);
        }
        i++;
        while (i < buf.Length && (buf[i] == ' ' || buf[i] == '\n' || buf[i] == '\t' || buf[i] == '\r'))
        {
            i++;
        }
        if (i >= buf.Length || buf[i] != '"')
        {
            return ("", false, false);
        }

        // value starts after the opening quote
        var start = i + 1;
        // Walk until an unescaped closing quote or end of buffer
        for (i = start; i < buf.Length; i++)
        {
            var c = buf[i];
            if (c == '\\')
            {
                // Skip escaped char if present
                if (i + 1 < buf.Length)
                {
                    if (buf[i + 1] == 'u')
                    {
                        // Attempt to skip \uXXXX if complete, otherwise break
                        if (i + 6 <= buf.Length)
                        {
                            i += 5; // loop will i++ -> total +6
                            continue;
                        }
                        // incomplete unicode escape
                        return (buf[start..i], false, true);
                    }
                    i++;
                    continue;
                }
                // trailing backslash
                return (buf[start..i], false, true);
            }
            if (c == '"')
            {
                // Found terminating quote
                return (buf[start..i], true, true);
            }
        }
        // Incomplete string reaches buffer end
        return (buf[start..], false, true);
    }

    /// <summary>
    /// decodePartialJSONString decodes a JSON string content (without surrounding quotes)
    /// while tolerating truncated/incomplete trailing escapes.
    /// </summary>
    internal static string DecodePartialJSONString(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "";

        var b = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\')
            {
                b.Append(c);
                continue;
            }
            // Escape sequence
            if (i + 1 >= s.Length)
            {
                // trailing backslash, stop
                break;
            }
            var esc = s[i + 1];
            switch (esc)
            {
                case '"':
                case '\\':
                case '/':
                    b.Append(esc);
                    i++;
                    break;
                case 'b':
                    b.Append('\b');
                    i++;
                    break;
                case 'f':
                    b.Append('\f');
                    i++;
                    break;
                case 'n':
                    b.Append('\n');
                    i++;
                    break;
                case 'r':
                    b.Append('\r');
                    i++;
                    break;
                case 't':
                    b.Append('\t');
                    i++;
                    break;
                case 'u':
                    if (i + 6 <= s.Length)
                    {
                        var hex = s.Substring(i + 2, 4);
                        if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
                        {
                            b.Append((char)v);
                            i += 5; // account for \uXXXX (loop adds +1)
                        }
                        else
                        {
                            // invalid hex, write literally
                            b.Append("\\u");
                            i++;
                        }
                    }
                    else
                    {
                        // incomplete unicode escape; stop
                        i = s.Length;
                    }
                    break;
                default:
                    // unknown escape, write literally
                    b.Append('\\');
                    // if last char was backslash and next is unknown, write it too if present
                    if (i + 1 < s.Length)
                    {
                        b.Append(esc);
                        i++;
                    }
                    break;
            }
        }
        return b.ToString();
    }

    /// <summary>
    /// extractPartialJSONStringArrayField finds a JSON array of strings under the given field
    /// name within a partial JSON object and returns the list of fully parsed elements
    /// encountered so far. The function is tolerant of truncated buffers and will stop
    /// before an incomplete string or missing closing bracket.
    /// </summary>
    internal static (List<string> values, bool complete, bool ok) ExtractPartialJSONStringArrayField(string buf, string field)
    {
        var values = new List<string>();
        var key = $"\"{field}\"";
        var idx = buf.LastIndexOf(key, StringComparison.Ordinal);
        if (idx == -1)
        {
            return (values, false, false);
        }

        var i = idx + key.Length;
        // skip to ':'
        while (i < buf.Length && (buf[i] == ' ' || buf[i] == '\n' || buf[i] == '\t' || buf[i] == '\r'))
        {
            i++;
        }
        if (i >= buf.Length || buf[i] != ':')
        {
            return (values, false, false);
        }
        i++;
        while (i < buf.Length && (buf[i] == ' ' || buf[i] == '\n' || buf[i] == '\t' || buf[i] == '\r'))
        {
            i++;
        }
        if (i >= buf.Length || buf[i] != '[')
        {
            return (values, false, false);
        }

        // move past '['
        i++;
        // Parse string entries until incomplete
        while (i < buf.Length)
        {
            // skip whitespace and commas
            while (i < buf.Length)
            {
                var c = buf[i];
                if (c == ' ' || c == '\n' || c == '\t' || c == '\r' || c == ',')
                {
                    i++;
                    continue;
                }
                break;
            }
            if (i >= buf.Length)
            {
                return (values, false, true);
            }
            if (buf[i] == ']')
            {
                return (values, true, true);
            }
            if (buf[i] != '"')
            {
                return (values, false, true);
            }

            // parse quoted string
            var start = i + 1;
            var j = start;
            while (j < buf.Length)
            {
                var c = buf[j];
                if (c == '\\')
                {
                    if (j + 1 < buf.Length)
                    {
                        if (buf[j + 1] == 'u')
                        {
                            if (j + 6 <= buf.Length)
                            {
                                j += 6;
                                continue;
                            }
                            return (values, false, true);
                        }
                        j += 2;
                        continue;
                    }
                    return (values, false, true);
                }
                if (c == '"')
                {
                    var raw = buf[start..j];
                    values.Add(DecodePartialJSONString(raw));
                    j++;
                    i = j;
                    break;
                }
                j++;
            }
            if (j >= buf.Length)
            {
                return (values, false, true);
            }
            // loop continues to parse next value or closing bracket
        }
        return (values, false, true);
    }
}
