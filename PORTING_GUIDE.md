# GoAgent to C# Porting Guide

This document tracks the progress of porting the GoAgent library from Go to C#.

## Overview

The GoAgent library consists of 56 Go files (32 source + 24 test files) that need to be ported to C#.

## Project Structure

### Source
- `Asynkron.Agent.Core` - Core library (class library targeting .NET 9.0)
- `Asynkron.Agent.Cli` - CLI application (console app targeting .NET 9.0)

### NuGet Packages Added
- `System.Threading.Channels` (10.0.2) - For Go channels
- `Newtonsoft.Json.Schema` (4.0.1) - For JSON schema validation
- `Microsoft.Extensions.Http` (10.0.2) - For HTTP client

## Porting Conventions

### Naming
- Go: `snake_case` or `camelCase` → C#: `PascalCase` for public, `camelCase` for private
- Go: `snake_case` for packages → C#: `PascalCase` for namespaces

### Concurrency
- Go `chan` → C# `System.Threading.Channels.Channel<T>`
- Go `goroutine` → C# `Task.Run()` or `async/await`
- Go `sync.Mutex` → C# `SemaphoreSlim` or `lock()`
- Go `sync.RWMutex` → C# `ReaderWriterLockSlim`
- Go `sync.Once` → C# `Lazy<T>` or manual with `lock`
- Go `context.Context` → C# `CancellationToken`

### Collections
- Go `[]T` (slice) → C# `List<T>` or `T[]`
- Go `map[K]V` → C# `Dictionary<K, V>`

### Error Handling
- Go multiple returns `(value, error)` → C# exceptions or `Result<T>` pattern
- Go `error` type → C# `Exception`

### JSON
- Go `encoding/json` → C# `System.Text.Json`
- Go struct tags → C# attributes like `[JsonPropertyName]`

## File Mapping

### Completed Files (22/56)

#### Runtime Package (7/23)
- [x] `types.go` → Multiple C# files:
  - `Runtime/MessageRole.cs`
  - `Runtime/ChatMessage.cs`
  - `Runtime/ToolCall.cs`
  - `Runtime/CommandDraft.cs`
  - `Runtime/PlanStatus.cs`
  - `Runtime/StepObservation.cs`
  - `Runtime/PlanObservationPayload.cs`
  - `Runtime/PlanObservation.cs`
  - `Runtime/PlanStep.cs`
  - `Runtime/PlanResponse.cs`
- [x] `events.go` → Multiple C# files:
  - `Runtime/EventType.cs`
  - `Runtime/StatusLevel.cs`
  - `Runtime/RuntimeEvent.cs`
  - `Runtime/InputEventType.cs`
  - `Runtime/InputEvent.cs`
- [x] `logger.go` → `Runtime/Logger.cs`
- [x] `metrics.go` → `Runtime/Metrics.cs`
- [x] `retry.go` → `Runtime/Retry.cs`
- [x] `context_budget.go` → `Runtime/ContextBudget.cs`
- [x] `system_prompt.go` → `Runtime/SystemPrompt.cs`
- [x] `plan_manager.go` → `Runtime/PlanManager.cs`

#### Schema Package (1/1)
- [x] `schema.go` → `Schema/PlanSchema.cs`

### Remaining Files (34/56)

#### Runtime Package (16 files)
- [ ] `options.go` → `Runtime/RuntimeOptions.cs` (225 lines)
- [ ] `runtime.go` → `Runtime/Runtime.cs` (236 lines)
- [ ] `loop.go` → `Runtime/RuntimeLoop.cs` (355 lines) - Main event loop
- [ ] `execution.go` → `Runtime/Execution.cs` (350 lines)
- [ ] `validation.go` → `Runtime/Validation.cs` (229 lines)
- [ ] `command_executor.go` → `Runtime/CommandExecutor.cs` (543 lines)
- [ ] `openai_client.go` → `Runtime/OpenAIClient.cs` (351 lines)
- [ ] `openai_request_builder.go` → `Runtime/OpenAIRequestBuilder.cs` (150 lines)
- [ ] `openai_stream_parser.go` → `Runtime/OpenAIStreamParser.cs` (285 lines)
- [ ] `history.go` → `Runtime/History.cs` (~150 lines)
- [ ] `history_amnesia.go` → `Runtime/HistoryAmnesia.cs` (~100 lines)
- [ ] `history_compactor.go` → `Runtime/HistoryCompactor.cs` (190 lines)
- [ ] `plan_execution.go` → `Runtime/PlanExecution.cs` (164 lines)
- [ ] `internal_command_apply_patch.go` → `Runtime/InternalCommandApplyPatch.cs` (174 lines)
- [ ] `internal_command_run_research.go` → `Runtime/InternalCommandRunResearch.cs` (~100 lines)

#### Patch Package (4 files)
- [ ] `doc.go` → Skip (just package docs)
- [ ] `parse.go` → `Patch/Parse.cs` (6971 bytes) - Unified diff parser
- [ ] `apply.go` → `Patch/Apply.cs` (8877 bytes) - Patch application logic
- [ ] `filesystem.go` → `Patch/Filesystem.cs` (7514 bytes)
- [ ] `memory.go` → `Patch/Memory.cs` (4236 bytes)

#### Bootprobe Package (4 files)
- [ ] `bootstrap.go` → `Bootprobe/Bootstrap.cs`
- [ ] `context.go` → `Bootprobe/Context.cs`
- [ ] `probes.go` → `Bootprobe/Probes.cs`

#### CLI Package (1 file)
- [ ] `cli.go` → `Cli/Cli.cs`

#### TUI Package (1 file)
- [ ] `tui.go` → `Tui/Tui.cs` - Requires TUI library (Spectre.Console or Terminal.Gui)

#### Entry Points (2 files)
- [ ] `cmd/main.go` → `Cli/Program.cs`
- [ ] `cmd/sse/main.go` → `Cli/SseProgram.cs` or separate project

### Test Files (24 files) - Port to xUnit
All `*_test.go` files need to be ported to xUnit test classes.

## Key Dependencies

### Go Dependencies to Replace
- `github.com/charmbracelet/bubbletea` → `Spectre.Console` or `Terminal.Gui`
- `github.com/charmbracelet/lipgloss` → Custom styling or Spectre.Console
- `github.com/charmbracelet/glamour` → Markdown rendering (custom or library)
- `github.com/xeipuuv/gojsonschema` → `Newtonsoft.Json.Schema`

## Implementation Notes

### RuntimeOptions
- File paths should use `Path.Combine()` instead of string concatenation
- Default stdin/stdout: `Console.OpenStandardInput()`, `Console.OpenStandardOutput()`

### Runtime
- Main runtime uses channels: `Channel<InputEvent>`, `Channel<RuntimeEvent>`
- Event loop should be `async Task Run(CancellationToken)`
- Use `Task.Run()` for background work

### CommandExecutor
- Process execution: Use `System.Diagnostics.Process`
- Shell detection: Platform-specific (cmd.exe on Windows, /bin/sh on Unix)

### OpenAIClient
- Use `HttpClient` with `IHttpClientFactory`
- SSE streaming: Parse `data:` events manually or use library
- Implement retry logic using the `Retry.cs` helpers

### History Management
- Token counting: Simple heuristic (chars / 4) or use tokenizer library
- Compaction: Use OpenAI API to summarize old messages

### Patch Package
- Unified diff parsing is complex - port algorithm carefully
- File operations: Use `System.IO.File`, `System.IO.Directory`

## Build and Test

```bash
# Build the solution
dotnet build Asynkron.Agent.sln

# Run tests (once ported)
dotnet test

# Run CLI
dotnet run --project src/Asynkron.Agent.Cli
```

## Next Steps

1. **Priority 1: Core Runtime** (most critical)
   - Port `RuntimeOptions.cs`
   - Port `Runtime.cs`
   - Port `RuntimeLoop.cs` (main event loop)
   - Port `Execution.cs`

2. **Priority 2: OpenAI Integration**
   - Port `OpenAIClient.cs`
   - Port `OpenAIRequestBuilder.cs`
   - Port `OpenAIStreamParser.cs`
   - Port `Validation.cs`

3. **Priority 3: Command Execution**
   - Port `CommandExecutor.cs`
   - Port internal command handlers

4. **Priority 4: History Management**
   - Port `History.cs`
   - Port `HistoryCompactor.cs`
   - Port `HistoryAmnesia.cs`

5. **Priority 5: Patch System**
   - Port Patch package files

6. **Priority 6: CLI and TUI**
   - Port CLI
   - Port or replace TUI with Spectre.Console

7. **Priority 7: Tests**
   - Port all test files to xUnit

## Code Examples

### Channel Usage
```csharp
// Go: make(chan InputEvent, 4)
var channel = Channel.CreateBounded<InputEvent>(new BoundedChannelOptions(4));

// Go: ch <- value
await channel.Writer.WriteAsync(value);

// Go: value := <-ch
var value = await channel.Reader.ReadAsync();

// Go: for value := range ch
await foreach (var value in channel.Reader.ReadAllAsync())
{
    // Process value
}
```

### RWMutex Usage
```csharp
private readonly ReaderWriterLockSlim _lock = new();

// Go: mu.RLock() / mu.RUnlock()
_lock.EnterReadLock();
try
{
    // Read operation
}
finally
{
    _lock.ExitReadLock();
}

// Go: mu.Lock() / mu.Unlock()
_lock.EnterWriteLock();
try
{
    // Write operation
}
finally
{
    _lock.ExitWriteLock();
}
```

### Context/Cancellation
```csharp
// Go: ctx, cancel := context.WithCancel(context.Background())
using var cts = new CancellationTokenSource();
var token = cts.Token;

// Go: defer cancel()
// In C#: Use using statement or call cts.Cancel() in finally

// Go: select { case <-ctx.Done(): ... }
token.ThrowIfCancellationRequested();
// or
if (token.IsCancellationRequested) { ... }
```

## Resources

- [Go to C# comparison](https://yourbasic.org/golang/nutshells/)
- [System.Threading.Channels documentation](https://learn.microsoft.com/en-us/dotnet/api/system.threading.channels)
- [Async/await best practices](https://learn.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming)
