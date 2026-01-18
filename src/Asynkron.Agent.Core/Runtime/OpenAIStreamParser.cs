using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// streamParser handles parsing of SSE (Server-Sent Events) streams from OpenAI.
/// </summary>
internal sealed class OpenAIStreamParser(StreamReader reader, Action<string>? onDelta, bool debugStream)
{
    private string _toolId = "";
    private string _toolName = "";
    private string _toolArgs = "";
    private string _lastEmittedMessage = "";
    private int _lastEmittedReasoningCount = 0;

    /// <summary>
    /// parse reads and parses the SSE stream until completion or error.
    /// </summary>
    public async Task<ToolCall> ParseAsync()
    {
        if (debugStream)
        {
            Console.WriteLine("====== STREAM: HTTP connected; starting SSE read loop");
        }

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line == null)
            {
                break; // EOF
            }

            line = line.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith(":"))
            {
                continue; // keepalive/comment
            }

            if (!line.StartsWith("data:") && !line.StartsWith("data: "))
            {
                continue;
            }

            var chunkData = line.StartsWith("data: ")
                ? line["data: ".Length..].Trim()
                : line["data:".Length..].Trim();

            if (chunkData == "[DONE]")
            {
                if (debugStream)
                {
                    Console.WriteLine("------ STREAM: [DONE]");
                }
                break;
            }

            var evt = ParseEvent(chunkData);
            if (evt == null)
            {
                continue;
            }

            ProcessEvent(evt);
        }

        if (!string.IsNullOrEmpty(_toolName))
        {
            return new ToolCall { ID = _toolId, Name = _toolName, Arguments = _toolArgs };
        }

        // No tool call is valid for plain text responses
        return new ToolCall();
    }

    /// <summary>
    /// parseEvent parses a single SSE data chunk into an event map.
    /// </summary>
    private Dictionary<string, object>? ParseEvent(string chunkData)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<Dictionary<string, object>>(chunkData);
            if (debugStream)
            {
                if (evt != null && evt.TryGetValue("type", out var t))
                {
                    var tStr = t?.ToString() ?? "?";
                    Console.WriteLine($"------ STREAM: {tStr}");
                }
                else
                {
                    Console.WriteLine("------ STREAM: event ?");
                }
            }
            return evt;
        }
        catch (JsonException ex)
        {
            if (debugStream)
            {
                var chunkPreview = chunkData.Length > 200 ? chunkData[..200] + "..." : chunkData;
                Console.WriteLine($"------ STREAM: decode-error {ex.Message} (chunk: \"{chunkPreview}\")");
            }
            return null;
        }
    }

    /// <summary>
    /// processEvent handles a single stream event and updates parser state.
    /// </summary>
    private void ProcessEvent(Dictionary<string, object> evt)
    {
        if (!evt.TryGetValue("type", out var typeObj))
            return;

        var t = typeObj?.ToString() ?? "";
        switch (t)
        {
            case "response.output_text.delta":
                HandleOutputTextDelta(evt);
                break;
            case "response.function_call.delta":
            case "response.tool_call.delta":
            case "message.function_call.delta":
            case "message.tool_call.delta":
            case "response_function_call_delta":
            case "response_tool_call_delta":
            case "response_function_call_arguments_delta":
                HandleFunctionCallDelta(evt);
                break;
            case "response.function_call.arguments.delta":
            case "response.tool_call.arguments.delta":
            case "message.function_call.arguments.delta":
            case "message.tool_call.arguments.delta":
            case "response.function_call_arguments.delta":
            case "response.tool_call_arguments.delta":
                HandleArgumentsDelta(evt);
                break;
            case "message.delta":
            case "response.message.delta":
                HandleMessageDelta(evt);
                break;
            case "response.completed":
            case "response.output_text.done":
            case "response.function_call.completed":
                HandleCompletion(evt);
                break;
        }
    }

    /// <summary>
    /// handleOutputTextDelta processes output text delta events.
    /// </summary>
    private void HandleOutputTextDelta(Dictionary<string, object> evt)
    {
        if (evt.TryGetValue("delta", out var deltaObj) && deltaObj is string s && !string.IsNullOrEmpty(s))
        {
            onDelta?.Invoke(s);
        }
        else if (deltaObj is JsonElement { ValueKind: JsonValueKind.String } deltaElem)
        {
            var str = deltaElem.GetString();
            if (!string.IsNullOrEmpty(str))
            {
                onDelta?.Invoke(str);
            }
        }
    }

    /// <summary>
    /// handleFunctionCallDelta processes function/tool call delta events.
    /// </summary>
    private void HandleFunctionCallDelta(Dictionary<string, object> evt)
    {
        if (evt.TryGetValue("name", out var nameObj))
        {
            var name = nameObj?.ToString() ?? "";
            if (!string.IsNullOrEmpty(name))
            {
                _toolName = name;
            }
        }

        if (evt.TryGetValue("call_id", out var idObj))
        {
            var id = idObj?.ToString() ?? "";
            if (!string.IsNullOrEmpty(id))
            {
                ResetCall(id);
            }
        }

        // Arguments may be provided as top-level "arguments" string, as a
        // raw delta string, or nested under a delta object.
        if (evt.TryGetValue("arguments", out var argsObj) && argsObj is string args && !string.IsNullOrEmpty(args))
        {
            _toolArgs += args;
            EmitMessageDelta(_toolArgs);
            EmitReasoningDeltas(_toolArgs);
        }
        else if (evt.TryGetValue("delta", out var deltaObj))
        {
            if (deltaObj is string ds && !string.IsNullOrEmpty(ds))
            {
                _toolArgs += ds;
                EmitMessageDelta(_toolArgs);
                EmitReasoningDeltas(_toolArgs);
            }
            else if (TryGetDictionary(deltaObj, out var dm))
            {
                if (dm.TryGetValue("arguments", out var deltaArgs) && deltaArgs is string s && !string.IsNullOrEmpty(s))
                {
                    _toolArgs += s;
                    EmitMessageDelta(_toolArgs);
                    EmitReasoningDeltas(_toolArgs);
                }
                if (dm.TryGetValue("name", out var deltaName))
                {
                    var n = deltaName?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(n))
                    {
                        _toolName = n;
                    }
                }
            }
        }
    }

    /// <summary>
    /// handleArgumentsDelta processes dedicated arguments delta events.
    /// </summary>
    private void HandleArgumentsDelta(Dictionary<string, object> evt)
    {
        if (evt.TryGetValue("delta", out var deltaObj) && deltaObj is string s && !string.IsNullOrEmpty(s))
        {
            _toolArgs += s;
            EmitMessageDelta(_toolArgs);
            EmitReasoningDeltas(_toolArgs);
        }
        else if (deltaObj is JsonElement { ValueKind: JsonValueKind.String } deltaElem)
        {
            var str = deltaElem.GetString();
            if (!string.IsNullOrEmpty(str))
            {
                _toolArgs += str;
                EmitMessageDelta(_toolArgs);
                EmitReasoningDeltas(_toolArgs);
            }
        }
    }

    /// <summary>
    /// handleMessageDelta processes message delta events.
    /// </summary>
    private void HandleMessageDelta(Dictionary<string, object> evt)
    {
        if (!evt.TryGetValue("delta", out var deltaObj))
            return;

        if (TryGetDictionary(deltaObj, out var dm))
        {
            if (dm.TryGetValue("type", out var typeObj) && typeObj?.ToString() == "output_text.delta")
            {
                if (dm.TryGetValue("text", out var textObj))
                {
                    var s = textObj?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(s))
                    {
                        onDelta?.Invoke(s);
                    }
                }
            }
        }
        else if (TryGetArray(deltaObj, out var arr))
        {
            foreach (var it in arr)
            {
                if (TryGetDictionary(it, out var m))
                {
                    if (m.TryGetValue("type", out var typeObj) && typeObj?.ToString() == "output_text.delta")
                    {
                        if (m.TryGetValue("text", out var textObj))
                        {
                            var s = textObj?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(s))
                            {
                                onDelta?.Invoke(s);
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// handleCompletion processes completion events and extracts final tool call data.
    /// </summary>
    private void HandleCompletion(Dictionary<string, object> evt)
    {
        if (string.IsNullOrEmpty(_toolArgs) || string.IsNullOrEmpty(_toolName) || string.IsNullOrEmpty(_toolId))
        {
            if (evt.TryGetValue("response", out var respObj) && TryGetDictionary(respObj, out var respDict))
            {
                if (string.IsNullOrEmpty(_toolName))
                {
                    if (FindStringInMap(respDict, "name", out var name))
                    {
                        _toolName = name;
                    }
                }
                if (string.IsNullOrEmpty(_toolId))
                {
                    if (FindStringInMap(respDict, "call_id", out var callId))
                    {
                        _toolId = callId;
                    }
                }
                if (string.IsNullOrEmpty(_toolArgs))
                {
                    if (FindStringInMap(respDict, "arguments", out var args))
                    {
                        _toolArgs = args;
                    }
                }
            }
        }
    }

    /// <summary>
    /// findStringInMap searches a nested map structure for a string value by key using DFS.
    /// </summary>
    private static bool FindStringInMap(object v, string key, out string result)
    {
        result = "";

        if (TryGetDictionary(v, out var dict))
        {
            if (dict.TryGetValue(key, out var val))
            {
                var s = val?.ToString() ?? "";
                if (!string.IsNullOrEmpty(s))
                {
                    result = s;
                    return true;
                }
            }
            foreach (var child in dict.Values)
            {
                if (FindStringInMap(child, key, out result))
                {
                    return true;
                }
            }
        }
        else if (TryGetArray(v, out var arr))
        {
            foreach (var child in arr)
            {
                if (FindStringInMap(child, key, out result))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// resetCall resets the parser state when a new tool call ID is observed.
    /// </summary>
    private void ResetCall(string newId)
    {
        if (!string.IsNullOrEmpty(newId) && newId != _toolId)
        {
            _toolId = newId;
            _toolArgs = "";
            _lastEmittedMessage = "";
            _lastEmittedReasoningCount = 0;
        }
    }

    /// <summary>
    /// emitMessageDelta extracts and emits the "message" field from partial JSON.
    /// </summary>
    private void EmitMessageDelta(string buf)
    {
        if (onDelta == null)
            return;

        var (raw, _, ok) = OpenAIClient.ExtractPartialJSONStringField(buf, "message");
        if (!ok)
            return;

        var decoded = OpenAIClient.DecodePartialJSONString(raw);
        if (string.IsNullOrEmpty(decoded))
            return;

        if (string.IsNullOrEmpty(_lastEmittedMessage))
        {
            onDelta(decoded);
            _lastEmittedMessage = decoded;
            return;
        }

        if (decoded.StartsWith(_lastEmittedMessage))
        {
            onDelta(decoded[_lastEmittedMessage.Length..]);
            _lastEmittedMessage = decoded;
        }
        else if (decoded != _lastEmittedMessage)
        {
            onDelta(decoded);
            _lastEmittedMessage = decoded;
        }
    }

    /// <summary>
    /// emitReasoningDeltas extracts and emits reasoning array entries.
    /// </summary>
    private void EmitReasoningDeltas(string buf)
    {
        if (onDelta == null)
            return;

        var (vals, _, ok) = OpenAIClient.ExtractPartialJSONStringArrayField(buf, "reasoning");
        if (!ok)
            return;

        if (_lastEmittedReasoningCount < vals.Count)
        {
            for (var i = _lastEmittedReasoningCount; i < vals.Count; i++)
            {
                var v = vals[i].Trim();
                if (!string.IsNullOrEmpty(v))
                {
                    onDelta("\n" + v);
                }
            }
            _lastEmittedReasoningCount = vals.Count;
        }
    }

    private static bool TryGetDictionary(object? obj, out Dictionary<string, object> dict)
    {
        dict = new Dictionary<string, object>();
        
        if (obj is Dictionary<string, object> d)
        {
            dict = d;
            return true;
        }

        if (obj is JsonElement { ValueKind: JsonValueKind.Object } elem)
        {
            dict = new Dictionary<string, object>();
            foreach (var prop in elem.EnumerateObject())
            {
                dict[prop.Name] = prop.Value;
            }
            return true;
        }

        return false;
    }

    private static bool TryGetArray(object? obj, out List<object> arr)
    {
        arr = [];

        if (obj is List<object> list)
        {
            arr = list;
            return true;
        }

        if (obj is object[] objArr)
        {
            arr = objArr.ToList();
            return true;
        }

        if (obj is JsonElement { ValueKind: JsonValueKind.Array } elem)
        {
            arr = [];
            foreach (var item in elem.EnumerateArray())
            {
                arr.Add(item);
            }
            return true;
        }

        return false;
    }
}
