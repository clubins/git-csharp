namespace Scion.Cli.Tests;

// Scaffold smoke test: proves the suite builds and executes under the real test runner.
// End-to-end CLI flows against a test server land alongside the scn commands.
public class SmokeTests
{
    [Fact]
    public void TestFrameworkExecutes()
    {
        Assert.Equal("Scion", "Sci" + "on");
    }
}
