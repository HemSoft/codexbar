# Runs ralph to improve react-doctor results on a dedicated feature branch.
$ErrorActionPreference = 'Stop'

ralph -Prompt "Improve the react-doctor results to return 100" -Branch "feature/improve-react-doctor" -CleanupWorktree -Max 10
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
