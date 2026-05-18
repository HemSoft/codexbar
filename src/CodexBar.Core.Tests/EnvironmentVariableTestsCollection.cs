// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

/// <summary>
/// Disables parallel execution for tests that manipulate process-wide state
/// such as environment variables.
/// </summary>
[CollectionDefinition("EnvironmentVariableTests", DisableParallelization = true)]
public class EnvironmentVariableTestsCollection
{
}
