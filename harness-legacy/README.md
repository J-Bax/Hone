> **ARCHIVED** — This directory contains the original PowerShell implementation of the Hone
> optimization harness. It has been superseded by the C# harness in `harness-csharp/`.
> The PowerShell harness is preserved here for reference and rollback purposes only.

## Rollback instructions

If you need to revert to the PowerShell harness:

```powershell
# Rename directories to restore the original layout
git mv harness-legacy harness

# Restore the git hooks from the last PowerShell-only commit
git checkout ps-harness-final -- .githooks/

# Fix the collector/analyzer paths in config.yaml to point to harness/ again
# (config.yaml was created in the cutover commit — there is no earlier version to check out)
(Get-Content harness-csharp/config.yaml) -replace 'harness-legacy/', 'harness/' |
    Set-Content harness-csharp/config.yaml

# Commit the revert
git commit --no-gpg-sign -m "revert: restore PowerShell harness"
```

The PowerShell harness is immediately runnable — all scripts and module files are intact.
Entry point: `harness/Invoke-HoneLoop.ps1`.

## Contents

| Path | Description |
|------|-------------|
| `collectors/` | PowerShell diagnostic collector plugins |
| `analyzers/` | PowerShell diagnostic analyzer plugins |
| `hooks/` | PowerShell lifecycle hook scripts |
| `tests/` | Test fixtures and integration tests |
| `Invoke-HoneLoop.ps1` | Main loop entry point |
| `config.psd1` | Original PowerShell configuration file |
