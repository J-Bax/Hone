# Lifecycle Hooks

Lifecycle hooks let target projects define **how** Hone interacts with their build system, runtime, and measurement infrastructure. Each hook maps to a specific phase of the optimization loop and can be implemented as a built-in C# handler, a shell command, an HTTP request, or explicitly skipped.

## Why Hooks Matter

The Hone harness is **target-agnostic** — it doesn't assume `dotnet build`, `dotnet test`, or any specific toolchain. Hooks are the contract between the harness and your target project. A .NET 6 API, a Java Spring Boot service, and a Python Flask app all use the same hook system with different implementations.

## Hook Types

Every hook in `.hone/config.yaml` declares a `Type` that determines how it executes:

| Type | Description | Required Fields |
|------|-------------|-----------------|
| `BuiltIn` | Delegates to a native C# hook implementation registered in the harness | `Name` — registry key (e.g., `dotnet-build`) |
| `Command` | Runs a shell command string via `cmd.exe` (Windows) or `/bin/sh` (Linux/macOS) | `Value` — the command to execute |
| `Http` | Makes an HTTP request to the running target API | `Path` — URL path (relative or absolute), optional `Method` (default: `GET`) |
| `Skip` | No-op — the hook phase is intentionally skipped | (none) |

## The 10 Lifecycle Hooks

Hooks are dispatched in the following order during the optimization loop:

### Setup Phase

| Hook | When | Purpose | Typical Implementation |
|------|------|---------|----------------------|
| **Prepare** | Once before the first experiment | Project-level setup: dependency restore, codegen, database seeding | `Command: 'pwsh -File .hone/hooks/prepare.ps1'` |

### Per-Experiment Phase

| Hook | When | Purpose | Typical Implementation |
|------|------|---------|----------------------|
| **Build** | After each code change is applied | Compile the target project | `BuiltIn: dotnet-build` or `Command: './gradlew build'` |
| **Test** | After a successful build | Run the regression test suite (must pass 100%) | `BuiltIn: dotnet-test` or `Command: 'pytest tests/'` |
| **Stop** | Before build (prevents file locks) and after failed experiments | Stop the running target API process | `BuiltIn: dotnet-stop` or `Command: 'docker stop myapi'` |
| **Start** | After build+test, before load testing | Start the target API process | `BuiltIn: dotnet-start` or `Command: 'docker start myapi'` |
| **Ready** | Immediately after Start | Health check — wait for the API to become responsive | `BuiltIn: health-poll` or `Http: GET /health` |

### Measurement Phase

| Hook | When | Purpose | Typical Implementation |
|------|------|---------|----------------------|
| **Warmup** | Before the scale test measurement cycle | Custom pre-measurement warmup (cache priming, JIT warmup, data loading) | `Skip` or `Command: 'curl http://localhost:5000/api/warmup'` |
| **Active** | During the active measurement phase | Represents the load test execution itself (currently handled implicitly by the k6 runner) | `BuiltIn: k6-run` |
| **Cooldown** | After each k6 run within a scale test | Inter-run cleanup: GC trigger, cache flush, connection pool reset | `Http: POST /diag/gc` or `Skip` |

### Teardown Phase

| Hook | When | Purpose | Typical Implementation |
|------|------|---------|----------------------|
| **Cleanup** | Once after all experiments complete | Final teardown: log collection, resource cleanup, notifications | `Skip` or `Command: 'pwsh -File .hone/hooks/cleanup.ps1'` |

### Hook Dispatch Status

| Hook | Status |
|------|--------|
| Prepare, Build, Test, Start, Stop, Ready | ✅ Dispatched |
| Warmup, Cooldown, Cleanup | ✅ Dispatched |
| Active | ⚠️ Implicit — the load test runner handles this phase. Full hook dispatch requires typed return values (future enhancement with #13). |

## Built-In Hooks Reference

These are registered in the harness and available via `Type: BuiltIn`:

| Name | Hook Phase | Description |
|------|-----------|-------------|
| `dotnet-build` | Build | Runs `dotnet build --no-restore -c Release` in the target directory |
| `dotnet-test` | Test | Runs `dotnet test --no-build -c Release` in the target directory |
| `dotnet-start` | Start | Launches the target via `dotnet run`, returns the allocated port as `BaseUrl` |
| `dotnet-stop` | Stop | Kills the running `dotnet` process for the target project |
| `health-poll` | Ready | Polls the configured health endpoint until it returns HTTP 200 (with timeout) |
| `k6-run` | Active | Runs the configured k6 scenario against the target API |

> **Note:** Built-in hooks are .NET-specific. Non-.NET targets should use `Command` or `Http` hooks instead.

## Configuration Examples

### .NET Target (using built-in hooks)

```yaml
# .hone/config.yaml — .NET 8 Web API
Name: "MyDotNetApi"
BaseBranch: "main"

Hooks:
  Prepare:
    Type: Command
    Value: 'dotnet restore'
  Build:
    Type: BuiltIn
    Name: dotnet-build
  Test:
    Type: BuiltIn
    Name: dotnet-test
  Start:
    Type: BuiltIn
    Name: dotnet-start
  Stop:
    Type: BuiltIn
    Name: dotnet-stop
  Ready:
    Type: BuiltIn
    Name: health-poll
  Warmup:
    Type: Skip
  Active:
    Type: BuiltIn
    Name: k6-run
  Cooldown:
    Type: Http
    Method: POST
    Path: "/diag/gc"
  Cleanup:
    Type: Skip
```

### Java Spring Boot Target (using command hooks)

```yaml
# .hone/config.yaml — Java Spring Boot API
Name: "MySpringApi"
BaseBranch: "main"

Hooks:
  Prepare:
    Type: Command
    Value: './gradlew dependencies'
  Build:
    Type: Command
    Value: './gradlew build -x test'
  Test:
    Type: Command
    Value: './gradlew test'
  Start:
    Type: Command
    Value: 'java -jar build/libs/myapi.jar --server.port=5050 &'
  Stop:
    Type: Command
    Value: 'pkill -f myapi.jar'
  Ready:
    Type: Http
    Method: GET
    Path: "/actuator/health"
  Warmup:
    Type: Skip
  Active:
    Type: BuiltIn
    Name: k6-run
  Cooldown:
    Type: Skip
  Cleanup:
    Type: Skip

# Disable .NET-specific diagnostic collectors
Diagnostics:
  CollectorSettings:
    perfview-cpu:
      Enabled: false
    perfview-gc:
      Enabled: false
    dotnet-counters:
      Enabled: false
```

### Python Flask Target (using command hooks)

```yaml
# .hone/config.yaml — Python Flask API
Name: "MyFlaskApi"
BaseBranch: "main"

Hooks:
  Prepare:
    Type: Command
    Value: 'pip install -r requirements.txt'
  Build:
    Type: Skip
  Test:
    Type: Command
    Value: 'pytest tests/ -v'
  Start:
    Type: Command
    Value: 'gunicorn app:app --bind 0.0.0.0:5050 --daemon --pid /tmp/gunicorn.pid'
  Stop:
    Type: Command
    Value: 'kill $(cat /tmp/gunicorn.pid)'
  Ready:
    Type: Http
    Method: GET
    Path: "/health"
  Warmup:
    Type: Command
    Value: 'curl -s http://localhost:5050/api/warmup > /dev/null'
  Active:
    Type: BuiltIn
    Name: k6-run
  Cooldown:
    Type: Skip
  Cleanup:
    Type: Command
    Value: 'rm -f /tmp/gunicorn.pid'

# Disable all .NET-specific diagnostics
Diagnostics:
  Enabled: false
```

## Diagnostic Overrides for Non-.NET Targets

By default, the engine enables PerfView (Windows-only) and dotnet-counters (.NET-only) collectors. Non-.NET targets must disable these in their `.hone/config.yaml`:

```yaml
# Disable specific collectors
Diagnostics:
  CollectorSettings:
    perfview-cpu:
      Enabled: false
    perfview-gc:
      Enabled: false
    dotnet-counters:
      Enabled: false

# Or disable the entire diagnostic framework
Diagnostics:
  Enabled: false
```

See [Configuration — Diagnostics](configuration.md#diagnostics-configuration) for the full diagnostic settings reference.

## Writing Custom Hooks

### Command Hooks

Command hooks run via the system shell (`cmd.exe /c` on Windows, `/bin/sh -c` on Unix). They support:
- Piping and redirection: `Value: 'npm test 2>&1 | tee test.log'`
- Environment variables: `Value: 'PORT=5050 npm start'`
- Script files: `Value: 'pwsh -File .hone/hooks/prepare.ps1'`

The hook succeeds if the command exits with code 0; any other exit code is treated as failure.

### HTTP Hooks

HTTP hooks make requests to the running target API. They require a `Path` (relative paths are resolved against the API's `BaseUrl`):

```yaml
Cooldown:
  Type: Http
  Method: POST
  Path: "/diag/gc"
```

Supported methods: `GET`, `POST`, `PUT`, `DELETE`, `PATCH`. Default is `GET`.

### Skip Hooks

Use `Type: Skip` when a lifecycle phase doesn't apply to your target:

```yaml
# Interpreted language — no build step needed
Build:
  Type: Skip
```

All 10 hooks must be declared in `.hone/config.yaml`. Use `Skip` for phases that don't apply.

## Validation

The `ConfigValidator` checks all hook declarations at startup:
- All 10 hooks must be declared (use `Skip` for unused phases)
- Hook types must be one of: `BuiltIn`, `Command`, `Http`, `Skip`
- `BuiltIn` hooks require a `Name` field
- `Command` hooks require a `Value` field
- `Http` hooks require a `Path` field

Run `hone validate --target <path>` to check your configuration before running experiments.
