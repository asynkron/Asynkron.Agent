# Asynkron.Agent - C# Port of GoAgent

This is a C# port of the [GoAgent](https://github.com/asynkron/goagent) library, an AI software agent runtime that plans and executes work.

## Current Status

**✅ CORE RUNTIME COMPLETE - BUILDS SUCCESSFULLY**

The core agent runtime has been fully ported from Go to C#. All essential components are in place and the solution compiles without errors.

### Completed Components
- ✅ **Runtime Core** - Event loop, plan execution, state management
- ✅ **Type System** - All events, messages, and plan structures
- ✅ **OpenAI Integration** - HTTP client with SSE streaming
- ✅ **History Management** - Conversation tracking with compaction and amnesia
- ✅ **Plan Manager** - Dependency resolution and step execution
 - ✅ **Command Executor** - Shell command execution with filtering
 - ✅ **Validation** - JSON schema validation for responses
 - ✅ **Patch System** - Unified diff parsing and application
- ✅ **Logger & Metrics** - Structured logging and telemetry
 - ✅ **CLI** - Command-line interface with hands-free and research modes
- ✅ **Retry Logic** - Exponential backoff for API failures

### Architecture

The runtime uses a channel-based architecture with:
- Separate input/output channels for event communication
- Async/await throughout for non-blocking I/O
- Immutable record types for configuration
- Factory methods for safe object creation

## Project Structure

```
src/
├── Asynkron.Agent.Core/           # Core library
│   ├── Runtime/                    # Main runtime
│   ├── Schema/                     # JSON schema
│   └── Patch/                      # Diff/patch utilities
└── Asynkron.Agent.Cli/             # CLI application
```

## Usage

### Building

```bash
# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Build for release
dotnet build -c Release
```

### Running the Agent

```bash
# Set your OpenAI API key
export OPENAI_API_KEY="sk-..."

# Run with a prompt
dotnet run --project src/Asynkron.Agent.Cli --prompt "Explain async/await"

# Run in hands-free research mode
dotnet run --project src/Asynkron.Agent.Cli --research '{"goal":"Find bugs in this code","turns":10}'

# Use a specific model
dotnet run --project src/Asynkron.Agent.Cli --model gpt-4o --prompt "Hello"
```

### CLI Options

```
--model <model>              OpenAI model identifier (default: gpt-4o)
--reasoning-effort <level>   Reasoning effort: low, medium, high
--augment <text>             Additional system prompt instructions
--openai-base-url <url>      Override OpenAI API base URL
--prompt <text>              Submit this prompt immediately
--research <json>            Hands-free mode: {"goal":"...","turns":N}
```

### Programmatic Usage

```csharp
using Asynkron.Agent.Core.Runtime;

// Create runtime options
var options = new RuntimeOptions
{
    ApiKey = "sk-...",
    Model = "gpt-4o",
    UseStreaming = true
};

// Create and run the agent
var agent = Runtime.Create(options);
var runTask = agent.RunAsync(cancellationToken);

// Listen to events
await foreach (var evt in agent.Outputs().ReadAllAsync(cancellationToken))
{
    Console.WriteLine($"[{evt.Type}] {evt.Message}");
}
```

## Dependencies

### NuGet Packages
- `System.Threading.Channels` (10.0.2) - Channel-based concurrency
- `Newtonsoft.Json.Schema` (4.0.1) - JSON schema validation
- `JsonSchema.Net` (7.2.3) - JSON schema utilities
- `Microsoft.Extensions.AI` (9.1.0-preview) - AI abstractions
- `Microsoft.Extensions.Http` (10.0.2) - HTTP client factory

## Next Steps

1. **Microsoft.Extensions.AI Integration** - Refactor OpenAIClient to use IChatClient abstraction
2. **Comprehensive Testing** - Port test files from Go
3. **Performance Optimization** - Profile and optimize hot paths
4. **Documentation** - Add XML docs and examples

## Porting Notes

This is a **verbatim port** from Go to C#. The logic, structure, and behavior closely match the original GoAgent implementation. Key adaptations:

- Go channels → C# `System.Threading.Channels`
- Go goroutines → C# `Task` and `async/await`
- Go `sync.RWMutex` → C# `ReaderWriterLockSlim`
- Go `context.Context` → C# `CancellationToken`
- Go error values → C# exceptions with try/catch

See [PORTING_GUIDE.md](PORTING_GUIDE.md) for detailed conventions.

## Original Repository

https://github.com/asynkron/goagent
