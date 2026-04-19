# Runs ralph to improve the GitHub scorecard score on a dedicated feature branch.
$ErrorActionPreference = 'Stop'

ralph -Prompt "Improve the scorecard score for this repo. Your report is at this url: https://upgraded-adventure-j192emp.pages.github.io/scorecard-summary.html" -Branch "feature/improve-scorecard" -CleanupWorktree -Max 10
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
