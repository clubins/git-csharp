namespace Scion.Server.Tests;

// Scaffold smoke test: proves the suite builds and executes under the real test runner.
// End-to-end API tests (WebApplicationFactory) land alongside the Scion.Server endpoints.
public class SmokeTests
{
    [Fact]
    public void TestFrameworkExecutes()
    {
        Assert.Equal("Scion", "Sci" + "on");
    }
}
