# Local CI entrypoint (Windows PowerShell / pwsh). Same pipeline as GitHub Actions. Usage: ./ci.ps1 [fsh-ci args]
$py = (Get-Command python -ErrorAction SilentlyContinue) ?? (Get-Command python3 -ErrorAction SilentlyContinue)
if (-not $py) { Write-Error "python not found"; exit 1 }
$args2 = if ($args.Count -eq 0) { @('run') } else { $args }
& $py.Source (Join-Path $PSScriptRoot 'tools/ci/fsh_ci.py') @args2
exit $LASTEXITCODE
