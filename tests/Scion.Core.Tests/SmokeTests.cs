namespace Scion.Core.Tests;

// Scaffold smoke test: proves the suite builds and executes under the real test runner.
// Real coverage lands alongside the Scion.Core features it verifies.
public class SmokeTests
{
    [Fact]
    public void TestFrameworkExecutes()
    {
        Assert.Equal("Scion", "Sci" + "on");
    }
}
