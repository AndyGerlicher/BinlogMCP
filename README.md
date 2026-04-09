# BinlogMcp

An MCP (Model Context Protocol) server for reading and analyzing MSBuild binary log files (.binlog).

## Overview

This server exposes 52 tools that allow AI assistants to analyze MSBuild binary logs, including:

- **Build Info** - Summaries, errors, warnings, properties, items
- **Performance** - Slowest targets/tasks, compiler timing, parallelism analysis, I/O bottlenecks
- **Dependencies** - Project graph, assembly references, NuGet packages
- **Comparison** - Diff two builds, incremental build analysis
- **Diagnostics** - Failure diagnosis with root cause detection and fix suggestions
- **Debugging** - Target execution reasons, skipped targets, property origins, import chains
- **Evaluation** - Flattened project view showing final properties, items, and imports

## Standalone Tools

### BinlogMCP Client - Interactive Build Investigator

An interactive REPL for analyzing MSBuild binlogs, powered by AIDavid - a virtual MSBuild debugging expert inspired by [David Federman's debugging methodology](https://dfederm.com/debugging-msbuild/). Uses the [GitHub Copilot SDK](https://github.com/github/copilot-sdk) for AI-powered analysis.

```bash
# Start interactive session with a binlog
dotnet run --project src/BinlogMcp.Client -- ./build.binlog

# Or launch and provide the path when prompted
dotnet run --project src/BinlogMcp.Client
```

**Requirements:** Authenticate with GitHub CLI first: `gh auth login`

Once loaded, you'll see an interactive prompt:

```

  ▐█▌ BinlogMCP ───────────────────────────────────
  🔨 Build Log Investigator
     inspired by dfederm.com/debugging-msbuild

     msbuild.binlog loaded
     50 analysis tools ready

  ───────────────────────────────────────────────────

  Quick start:

    [1] diagnose            Automated build analysis
    [2] visualize timeline  Gantt chart of execution
    [3] visualize slowest   Slowest targets chart
    [4] help                All commands

  Or ask: "Why did this build fail?"

  ───────────────────────────────────────────────────

binlog>
```

**Interactive Commands:**
- `diagnose` - Run automated AIDavid diagnosis
- `visualize timeline` - Open Gantt chart of build execution in browser
- `visualize slowest` - Open bar chart of slowest targets
- `set baseline <path>` - Set a baseline binlog for comparisons
- `compare` - Compare current build with baseline
- `visualize comparison` - Visual comparison chart (requires baseline)
- `help` - Show available commands
- `exit` - Exit the program

**Or just ask questions naturally:**
- "Why did this build fail?"
- "What targets took the longest?"
- "Show me the errors"
- "What version of Newtonsoft.Json is being used?"

The client maintains conversation history, so you can ask follow-up questions that reference previous answers.

**Model Configuration:**
- The default model is `claude-opus-4.7`, configured in the session setup.

**Logging:** All LLM and tool interactions are logged to `binlog-client.log` in the current directory.

Requires GitHub CLI authentication (`gh auth login`) for Copilot SDK model access.

### Casing Analyzer

Fixes path casing in MSBuild source files. On Windows, incorrect casing in paths (e.g., `..\librarya\` instead of `..\LibraryA\`) can cause cache issues with NuGet and other tools.

```bash
# Fix casing in all .props, .targets, *proj files in a repo
dotnet run --project src/BinlogMcp.CasingAnalyzer -- /path/to/repo
```

The tool walks the filesystem to get canonical casing (disk is the source of truth), then fixes any path segments in source files that don't match.

## Requirements

- .NET 10 SDK (pinned via `global.json` with `rollForward: latestFeature`)

## Building

```bash
dotnet build
```

The repo uses Central Package Management (`Directory.Packages.props`), Nerdbank.GitVersioning, ReferenceTrimmer, and `TreatWarningsAsErrors`. All builds must be warning-free.

## Running

```bash
dotnet run --project src/BinlogMcp
```

## Testing

```bash
# Run unit tests (~293 tests)
dotnet test tests/BinlogMcp.Tests

# Run LLM integration tests (requires OpenAI API key, costs money - run sparingly)
dotnet test tests/BinlogMcp.IntegrationTests
```

Integration tests require an OpenAI API key. Configure via user secrets:
```bash
dotnet user-secrets set "OpenAI:ApiKey" "sk-..." --project tests/BinlogMcp.IntegrationTests
```

Or via environment variable: `OPENAI_API_KEY`. Tests skip automatically if no key is configured.

The interactive client (BinlogMcp.Client) uses the GitHub Copilot SDK instead and requires `gh auth login`.

## MCP Configuration

Add to your MCP client configuration (e.g., Claude Desktop):

```json
{
  "mcpServers": {
    "binlog": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/binlog-mcp/src/BinlogMcp"]
    }
  }
}
```

## Available Tools

| Tool | Description |
|------|-------------|
| `GetCacheStats` | Gets binlog cache statistics and optionally clears the cache |
| `ListBinlogs` | Lists all binlog files in a directory (with optional recursive search) |
| `GetBuildSummary` | Gets build summary: result, duration, error/warning counts, projects |
| `GetErrors` | Extracts all errors with file, line, column, code, and message |
| `GetWarnings` | Extracts all warnings with the same detail as errors |
| `GetTargets` | Gets target execution details sorted by duration (slowest first) |
| `GetTasks` | Gets task execution details with aggregation by task type |
| `GetCriticalPath` | Identifies targets on the critical path that determined build duration |
| `GetProjectDependencies` | Gets project dependency graph, build order, and parallel execution info |
| `SearchBinlog` | Searches binlog content for messages, errors, warnings, targets, tasks, or properties |
| `GetProperties` | Gets MSBuild properties with optional filtering and highlights important ones |
| `GetItems` | Gets MSBuild item groups (Compile, Reference, PackageReference, etc.) |
| `CompareBinlogs` | Compares two binlogs showing timing changes, new/fixed errors, and target differences |
| `DiffProperties` | Compares property values between builds - added, removed, changed properties |
| `DiffItems` | Compares items between builds - added/removed files, package version changes |
| `DiffTargetExecution` | Compares target execution between builds - what ran differently |
| `DiffImports` | Compares import chains between builds - .props/.targets file changes |
| `GetIncrementalBuildAnalysis` | Analyzes incremental build behavior - executed vs skipped targets |
| `GetNuGetRestoreAnalysis` | Analyzes NuGet restore - packages, timing, and any restore issues |
| `GetAssemblyReferences` | Gets assembly and project references with metadata |
| `GetPerformanceReport` | Comprehensive performance analysis - bottlenecks, slow targets/tasks, optimization hints |
| `GetCompilerPerformance` | Detailed C#/VB/F# compilation timing analysis |
| `GetParallelismAnalysis` | Build parallelism efficiency - concurrent operations, sequential bottlenecks |
| `GetSlowOperations` | Analyzes slow file I/O operations (Copy, Move, Delete, Exec) |
| `GetProjectPerformance` | Per-project timing rollup - identify which projects are slowest |
| `ComparePerformance` | Focused performance comparison between builds - timing regressions/improvements |
| `GetParallelismBlockers` | Identifies what's blocking parallelism - serialization points, dependency bottlenecks |
| `AnalyzeTarget` | Deep dive into a single target - tasks, parameters, I/O, timing breakdown |
| `GetFailureDiagnosis` | Analyzes build failures - categorizes errors, identifies root causes, suggests fixes |
| `GetDuplicateFileWrites` | Detects files written multiple times during build (wasteful I/O) |
| `GetPropertyReassignments` | Finds MSBuild properties set multiple times (conflicts/overrides) |
| `GetRedundantOperations` | Detects tasks running with identical inputs (wasted work) |
| `GetUnusedProjectOutputs` | Finds projects built but whose outputs aren't referenced (dead code) |
| `GetTargetDependencyGraph` | Analyzes target dependencies, finds circular and redundant deps |
| `GetWarningTrendsAnalysis` | Categorizes warnings, suggests bulk fixes and suppressions |
| `GetFileAccessPatterns` | Identifies frequently read files and caching opportunities |
| `GetSdkFrameworkMismatch` | Detects SDK/framework version conflicts across projects |
| `GetTargetExecutionReasons` | Shows why targets executed (DependsOnTargets, BeforeTargets, AfterTargets) |
| `GetSkippedTargets` | Lists targets that were skipped and explains why |
| `GetPropertyOrigin` | Traces property values back to their source file and location |
| `GetImportChain` | Shows the import hierarchy (.props/.targets files) for projects |
| `TraceProperty` | Full property evaluation trace: initial → each assignment → final |
| `TraceItem` | Track items through build (consumed, transformed, output) |
| `GetItemTransforms` | Show item transformations within targets |
| `GetMSBuildTaskCalls` | Show MSBuild task invocations between projects |
| `GetEnvironmentVariables` | Extract environment variables used during the build |
| `GetItemMetadata` | Deep dive into item metadata (versions, HintPaths, CopyLocal settings) |
| `GetTargetInputsOutputs` | Show target incremental build inputs/outputs for debugging re-runs |
| `GetTimeline` | Export timeline data for external visualization tools |
| `GetEvaluatedProject` | Shows flattened project view - final properties, items, imports after evaluation |
| `ListEmbeddedSourceFiles` | Lists all embedded source files in the binlog's source archive (.csproj, .props, .targets, etc.) |
| `GetEmbeddedSourceFile` | Reads the content of a specific embedded source file from the binlog |

## Output Formats

21 high-value tools support multiple output formats via a `format` parameter:

| Format | Description |
|--------|-------------|
| `json` | Default. Structured JSON for programmatic use |
| `markdown` | Human-readable reports with tables and bullet lists |
| `csv` | Tabular data for spreadsheet import |
| `timeline` | JSON format for timing visualization |

Tools supporting formats: GetBuildSummary, GetErrors, GetWarnings, GetTargets, GetTasks, GetCriticalPath, GetPerformanceReport, GetParallelismAnalysis, GetFailureDiagnosis, CompareBinlogs, DiffProperties, DiffTargetExecution, GetProjectDependencies, GetAssemblyReferences, GetProperties, GetItems, GetEvaluatedProject, GetProjectPerformance, ComparePerformance, GetParallelismBlockers, AnalyzeTarget.

## Performance

Parsed binlogs are cached in memory for ~18x faster repeated access. This means calling multiple tools on the same binlog only parses it once.

**Configuration (optional):**
- `BINLOG_CACHE_SIZE=10` - Maximum binlogs to cache (default: 10)
- `BINLOG_CACHE_ENABLED=false` - Disable caching

Use `GetCacheStats` to view cache status or clear it.

## Example Usage

Once configured, you can ask your AI assistant questions like:

- "List the binlog files in C:\builds"
- "What errors are in the latest build?"
- "Which targets took the longest to execute?"
- "What's on the critical path of this build?"

## Generating Binlog Files

To create a binlog from any MSBuild/dotnet build:

```bash
dotnet build -bl                    # Creates msbuild.binlog
dotnet build -bl:mybuild.binlog     # Custom filename
msbuild MySolution.sln -bl          # Works with msbuild too
```

## Dependencies

- [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) - MCP SDK for .NET
- [MSBuild.StructuredLogger](https://www.nuget.org/packages/MSBuild.StructuredLogger) - Library for reading binlog files

## License

MIT
