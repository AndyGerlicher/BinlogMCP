# BinlogMCP Backlog

Ideas and future work for the project.

## 🔥 High Priority

### Performance & Scale
- [ ] **Large binlog handling** - Streaming/chunked parsing for >1GB binlogs to reduce memory pressure
- [ ] **Lazy loading** - Only parse sections of binlog as needed instead of full upfront parse
- [ ] **Parallel parsing** - Leverage multiple cores for faster initial load

### New Tools
- [ ] **GetBuildHistory** - Compare multiple binlogs over time (trend analysis)
- [ ] **GetCodeCoverage** - If coverage data is embedded, extract and summarize
- [ ] **GetTestResults** - Extract test execution results from binlog
- [ ] **GetCopyOperations** - Detailed analysis of file copy tasks (source→dest, sizes, timing)
- [ ] **GetUnusedImports** - Detect .props/.targets files imported but not contributing

### Client Improvements
- [ ] **Watch mode** - Auto-reload when binlog file changes on disk
- [ ] **Export conversation** - Save Q&A session to markdown file
- [ ] **Bookmarks** - Save and recall interesting findings
- [ ] **Side-by-side comparison view** - Compare two builds interactively

## 🚀 Medium Priority

### Visualization
- [ ] **Dependency graph visualization** - Interactive project/target dependency graphs
- [ ] **Flame graph** - Hierarchical time visualization for build execution
- [ ] **Sankey diagram** - Show flow of items through transforms
- [ ] **Treemap** - Visualize time spent per project/target as nested rectangles
- [ ] **Web dashboard** - Standalone web UI for visualization (not just browser temp files)

### Analysis Enhancements
- [ ] **Machine learning anomaly detection** - Flag unusual build patterns
- [ ] **Build fingerprinting** - Detect if two builds are functionally equivalent
- [ ] **Predictive build time** - Estimate build time based on file changes
- [ ] **Resource utilization** - Correlate build phases with CPU/memory/disk usage
- [ ] **Compiler flag analysis** - Detect suboptimal compiler settings

### Output Formats
- [ ] **HTML report** - Self-contained HTML with embedded charts
- [ ] **SARIF** - Standard format for errors/warnings (IDE integration)
- [ ] **JUnit XML** - For CI/CD pipeline integration
- [ ] **Prometheus metrics** - Export build metrics for monitoring

### Developer Experience
- [ ] **VS Code extension** - View binlog analysis in editor
- [ ] **CLI mode** - Non-interactive command-line for scripting (`binlog-mcp analyze build.binlog --errors`)
- [ ] **GitHub Action** - Analyze binlogs in CI and post results to PR

## 💡 Ideas / Research

### Integration
- [ ] **Azure DevOps integration** - Pull binlogs from pipeline artifacts
- [ ] **Build server plugins** - Jenkins, TeamCity, etc.
- [ ] **Slack/Teams bot** - Query build status from chat

### Advanced Analysis
- [ ] **Incremental build debugging wizard** - Step-by-step guide to fix incremental build issues
- [ ] **Build optimization suggestions** - AI-powered recommendations based on patterns
- [ ] **Cross-solution analysis** - Analyze builds across multiple repos
- [ ] **Historical regression detection** - Alert when builds get slower over time

### Casing Analyzer Extensions
- [ ] **Dry-run mode** - Show what would be fixed without changing files
- [ ] **Git integration** - Auto-commit fixes with descriptive message
- [ ] **CI check** - Fail build if casing issues detected

### Documentation
- [ ] **Tool cookbook** - Common scenarios with example queries
- [ ] **Video tutorials** - Walkthrough of debugging real build issues
- [ ] **Architecture docs** - How the parser and caching work

## 🐛 Known Issues / Tech Debt

- [ ] **Test coverage gaps** - Add tests for edge cases in comparison tools
- [ ] **Error handling** - More graceful handling of corrupted binlogs
- [ ] **Logging consistency** - Standardize logging across all tools
- [ ] **Tool documentation** - Add XML docs to all public methods

## ✅ Recently Completed

_(Move items here when done)_

---

## Contributing

Pick an item, open an issue to discuss, then submit a PR!
