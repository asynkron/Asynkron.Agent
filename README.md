# Asynkron.Agent - C# Port of GoAgent

This is a C# port of the [GoAgent](https://github.com/asynkron/goagent) library, an AI software agent runtime that plans and executes work.

## Current Status

**⚠️ WORK IN PROGRESS ⚠️**

This is a partial port currently containing foundational types and infrastructure. See [PORTING_GUIDE.md](PORTING_GUIDE.md) for detailed progress.

### Completed (23/56 files)
- ✅ Core type definitions (events, messages, plan structures)
- ✅ Logger infrastructure  
- ✅ Metrics collection
- ✅ Retry logic
- ✅ JSON schema definitions
- ✅ Context budget tracking
- ✅ System prompts
- ✅ Plan manager
- ✅ Runtime options

### In Progress (33/56 files)
- 🔨 Runtime main loop and execution
- 🔨 OpenAI client integration
- 🔨 Command executor
- 🔨 History management and compaction
- 🔨 Validation logic
- 🔨 Patch parsing and application
- 🔨 Bootprobe, CLI, and TUI

## Project Structure

```
src/
├── Asynkron.Agent.Core/          # Core library
│   ├── Runtime/                   # Main runtime package (23 files)
│   ├── Schema/                    # JSON schema definitions (1 file)
│   ├── Patch/                     # Diff/patch utilities (4 files TODO)
│   └── Bootprobe/                 # Bootstrap utilities (4 files TODO)
└── Asynkron.Agent.Cli/            # CLI application (TODO)
```

See [PORTING_GUIDE.md](PORTING_GUIDE.md) for complete file mapping.

## Building

```bash
# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run tests (when ported)
dotnet test
```

## Dependencies

### NuGet Packages
- `System.Threading.Channels` (10.0.2) - Channel-based concurrency
- `Newtonsoft.Json.Schema` (4.0.1) - JSON schema validation
- `Microsoft.Extensions.Http` (10.0.2) - HTTP client factory

## Porting from Go

See [PORTING_GUIDE.md](PORTING_GUIDE.md) for comprehensive porting conventions.

### Quick Reference
```csharp
// Go channels → C# Channels
var channel = Channel.CreateBounded<T>(capacity);
await channel.Writer.WriteAsync(item);

// Go goroutines → C# Tasks
Task.Run(() => DoWork());

// Go sync.RWMutex → C# ReaderWriterLockSlim
_lock.EnterReadLock();
try { /* read */ }
finally { _lock.ExitReadLock(); }
```

## How to Complete the Port

1. Read [PORTING_GUIDE.md](PORTING_GUIDE.md)
2. Port files in priority order (documented in guide)
3. Run tests as you go
4. Match Go logic verbatim - this is a direct port

## Original Repository

https://github.com/asynkron/goagent
AI Agent built in C#
