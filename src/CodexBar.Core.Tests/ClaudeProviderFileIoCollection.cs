// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

/// <summary>
/// Disables parallel execution for tests that manipulate static path overrides
/// on ClaudeProvider, preventing cross-test interference.
/// </summary>
[CollectionDefinition("ClaudeProviderFileIo", DisableParallelization = true)]
public class ClaudeProviderFileIoCollection
{
}
