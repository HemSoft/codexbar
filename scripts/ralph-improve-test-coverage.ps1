# Runs ralph to increase test coverage on a dedicated feature branch.
$ErrorActionPreference = 'Stop'

ralph -Prompt "Increase test coverage to 100%" -Branch "feature/increase-coverage" -CleanupWorktree -Max 20
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
