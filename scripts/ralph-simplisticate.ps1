# Runs ralph to reduce complexity and code smells on a dedicated feature branch.
$ErrorActionPreference = 'Stop'

ralph -Prompt "Find patterns of complexity or code repetition and traditional code smells and fix them." -Branch "feature/simplisticate" -CleanupWorktree -Max 10
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
