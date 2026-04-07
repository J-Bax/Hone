# Windows Environment Tuning for Load Testing

When running the Hone harness on Windows with high-concurrency k6 scenarios (≥100 VUs), TCP socket exhaustion can cause progressive performance degradation across experiments.

## The Problem

Each k6 invocation creates thousands of TCP connections. On Windows, closed sockets enter `TIME_WAIT` state for **240 seconds** by default. With repeated load test runs (6 per experiment × 5 experiments = 30 runs), ephemeral ports become exhausted, causing connection queueing and inflated latency measurements.

**Symptoms:**
- P95 latency increases progressively across experiments despite identical code
- Later experiments show higher latency than earlier ones
- k6 processes may hang or time out

## Recommended Registry Settings

> ⚠️ These changes require administrator privileges and a system restart.

### Reduce TIME_WAIT Duration

Default: 240 seconds → Recommended: 30 seconds

```
HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters
  TcpTimedWaitDelay = 30 (DWORD, decimal)
```

### Expand Ephemeral Port Range

Default: 49152–65535 (16,384 ports) → Recommended: 10000–65534 (55,534 ports)

```powershell
netsh int ipv4 set dynamicport tcp start=10000 num=55534
netsh int ipv6 set dynamicport tcp start=10000 num=55534
```

### Verify Current Settings

```powershell
# Check TIME_WAIT duration
Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters' -Name TcpTimedWaitDelay -ErrorAction SilentlyContinue

# Check ephemeral port range
netsh int ipv4 show dynamicport tcp

# Count current TIME_WAIT sockets
(Get-NetTCPConnection -State TimeWait).Count
```

## Harness Configuration Mitigations

The harness includes built-in mitigations that reduce (but don't eliminate) socket pressure:

| Setting | Location | Effect |
|---------|----------|--------|
| `ExperimentCooldownSeconds: 30` | `LoopConfig` | Waits between experiments for sockets to clear |
| `CooldownSeconds: 3` | `ScaleTestConfig` | Waits between k6 runs within an experiment |
| `noVUConnectionReuse: true` | `baseline.js` | Closes connections between VU iterations |
| `GcEndpoint: "/diag/gc"` | `ApiConfig` | Triggers server-side GC between runs |

For Windows environments with default TCP settings, consider increasing `ExperimentCooldownSeconds` to 60–120 seconds.
