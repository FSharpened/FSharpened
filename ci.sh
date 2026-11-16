#!/bin/sh
# Local CI entrypoint (Linux/macOS/Git Bash). Same pipeline as GitHub Actions. Usage: ./ci.sh [fsh-ci args]
PY=$(command -v python3 || command -v python) || { echo "python not found"; exit 1; }
exec "$PY" "$(dirname "$0")/tools/ci/fsh_ci.py" "${@:-run}"
