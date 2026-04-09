# Copilot Instructions for BinlogMCP

## Build & Test

```bash
dotnet build                                    # Build all projects
dotnet test tests/BinlogMcp.Tests               # Run unit tests (~293 tests)
dotnet test tests/BinlogMcp.Tests --filter "FullyQualifiedName~GetBuildSummary"  # Run single test class
dotnet test tests/BinlogMcp.Tests --filter "DisplayName~GetBuildSummary_RealBinlog"  # Run single test
```

Integration tests (`tests/BinlogMcp.IntegrationTests`) call OpenAI and cost money — only run when explicitly needed. They require `OPENAI_API_KEY` or user secrets.

## Build System

The repo uses modern .NET build infrastructure:

- **Central Package Management** — All package versions in `Directory.Packages.props` (uses `ManagePackageVersionsCentrally` due to .NET 10 preview SDK)
- **Nerdbank.GitVersioning** — Automatic versioning via `version.json` (added as `GlobalPackageReference`)
- **ReferenceTrimmer** — Detects unused package/project references (added as `GlobalPackageReference`)
- **TreatWarningsAsErrors** — All builds must be warning-free
- **EnforceCodeStyleInBuild** — IDE analyzers (including IDE0005 unnecessary usings) run during build
- **GenerateDocumentationFile** — Enabled globally for ReferenceTrimmer accuracy
- **global.json** — Pins to .NET 10 SDK with `rollForward: latestFeature`

Common properties (`TargetFramework`, `ImplicitUsings`, `Nullable`, `IsPackable`, `TreatWarningsAsErrors`) are set in the root `Directory.Build.props` — do not duplicate in csproj files.

The `test-data/` directory has empty `Directory.Build.props` and `Directory.Packages.props` to isolate test fixture projects from the real build configuration.

## Architecture

This is an MCP (Model Context Protocol) server that exposes 52 tools for analyzing MSBuild binary log files (.binlog). It targets .NET 10.

**Projects:**
- `src/BinlogMcp` — The MCP server. Uses stdio transport, auto-discovers tools via `WithToolsFromAssembly()`.
- `src/BinlogMcp.Client` — Interactive REPL that wraps the MCP server as a subprocess and drives it with the GitHub Copilot SDK. Top-level statements in `Program.cs`.
- `src/BinlogMcp.Visualization` — HTML/JS chart rendering (Gantt timelines, bar charts) opened in-browser.
- `src/BinlogMcp.CasingAnalyzer` — Standalone CLI to fix path casing in MSBuild source files.

**Key design: All MCP tools are static methods on `BinlogTools`**, a single `partial class` split across files by domain:
- `BinlogTools.cs` — Core tools (summary, errors, warnings) + all shared helpers
- `BinlogTools.Performance.cs` — Timing, critical path, parallelism
- `BinlogTools.Comparison.cs` — Diff two builds
- `BinlogTools.Debugging.cs` — Target reasons, skipped targets, property origins
- `BinlogTools.Dependencies.cs` — Project graph, references, NuGet
- `BinlogTools.Diagnosis.cs` — Failure root cause analysis
- `BinlogTools.Search.cs` — Content search, properties, items
- `BinlogTools.ItemFlow.cs` — Item tracing and transforms
- `BinlogTools.FileIO.cs` — Duplicate writes, file access patterns
- `BinlogTools.Warnings.cs` — Warning categorization
- `BinlogTools.Environment.cs` — Env vars, timeline export
- `BinlogTools.Evaluation.cs` — Flattened project view
- `BinlogTools.Sources.cs` — Embedded source file access

## Conventions

### Adding a new MCP tool

1. Add a `public static string` method to the appropriate `BinlogTools.*.cs` partial class file
2. Annotate with `[McpServerTool]` and `[Description("...")]` (from `System.ComponentModel`)
3. Annotate each parameter with `[Description("...")]`
4. Use `ExecuteBinlogTool(binlogPath, build => { ... })` for single-binlog tools, or `ExecuteBinlogToolWithFormat(...)` if the tool supports output formats
5. Use `ExecuteBinlogComparisonWithFormat(...)` for tools that compare two binlogs
6. Return anonymous objects — the helpers serialize them to JSON

### Adding a new NuGet package

Add the version to `Directory.Packages.props` as a `<PackageVersion>` entry, then reference it in the csproj without a `Version` attribute.

### Error handling pattern

Tools never throw. The `ExecuteBinlogTool` / `ExecuteBinlogToolWithFormat` helpers catch exceptions and return `{ "error": "..." }` JSON. Use `ValidateFileExists()` / `ValidateDirectoryExists()` for path validation before loading.

### Output format support

Tools that support multi-format output accept a `string format = "json"` parameter and follow this pattern:
```csharp
var formatError = TryParseFormatWithError(format, out var outputFormat);
if (formatError != null) return formatError;
return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Title", build => { ... });
```

### Binlog access

Always use `BinlogCache.GetOrLoad(binlogPath)` (the `ExecuteBinlog*` helpers do this automatically). Never call `BinaryLog.ReadBuild()` directly — the cache provides ~18x faster repeated access.

### Test conventions

- Tests use xUnit and call `BinlogTools` methods directly (they're static)
- Tests use `FindTestBinlog()` to locate `test.binlog` by walking up from the output directory
- Tests that need a real binlog skip silently if `test.binlog` isn't found
- Parse tool output with `JsonDocument.Parse(result)` and assert on the JSON structure

### Extension methods

`BinlogExtensions` provides shared helpers: `MatchesFilter()` / `FailsFilter()` for case-insensitive filtering, `ToTimeString()` / `ToDateTimeString()` for consistent DateTime formatting.

### Client architecture

The Client project (`BinlogMcp.Client`) uses top-level statements in `Program.cs` and spawns the MCP server as a child process via `McpClient`. It uses the GitHub Copilot SDK with tool-calling in an agentic loop. `ConversationManager` maintains multi-turn context.
