namespace Scion.Cli.Tests;

/// <summary>
/// All CLI test classes share this collection so they never run concurrently: the environment
/// override tests mutate process-global environment variables, and the e2e tests read them.
/// </summary>
[CollectionDefinition("ScionCliSequential")]
public sealed class ScionCliSequentialCollection;
