using Scion.Cli.Local;
using Scion.Server.Tests;

namespace Scion.Cli.Tests;

/// <summary>
/// Environment overrides over the config file, exercised through real commits. All environment
/// manipulation lives in this one class — environment variables are process-global, every value
/// is restored in a finally block, and xunit runs one class's tests sequentially.
/// </summary>
[Collection("ScionCliSequential")]
public sealed class EnvironmentOverrideTests : IDisposable
{
    private readonly CliHarness harness = new();

    [Fact]
    public void Commit_UsesTheTokenFromTheEnvironment_WhenTheConfigHasNone()
    {
        harness.RunCli("init", "--server", harness.ServerUrl, "--name", "A", "--email", "a@b.c");
        Write("file.txt", "content");

        var (exit, _) = WithEnvironment(
            [(ScionEnvironment.TokenVariable, ScionServerFactory.TestToken)],
            () => harness.RunCli("commit", "-m", "env token"));
        Assert.Equal(0, exit);
    }

    [Fact]
    public void Commit_TokenInEnvironment_WinsOverTheConfig()
    {
        harness.RunCli("init", "--server", harness.ServerUrl, "--token", "wrong-token", "--name", "A", "--email", "a@b.c");
        Write("file.txt", "content");

        var (exit, _) = WithEnvironment(
            [(ScionEnvironment.TokenVariable, ScionServerFactory.TestToken)],
            () => harness.RunCli("commit", "-m", "env wins"));
        Assert.Equal(0, exit);
    }

    [Fact]
    public void Commit_WithAWrongToken_FailsWithTheCredentialHint()
    {
        harness.RunCli("init", "--server", harness.ServerUrl, "--token", "wrong-token", "--name", "A", "--email", "a@b.c");
        Write("file.txt", "content");

        var (exit, output) = harness.RunCli("commit", "-m", "bad token");
        Assert.Equal(1, exit);
        Assert.Contains("401", output);
        Assert.Contains(ScionEnvironment.TokenVariable, output);
    }

    [Fact]
    public async Task Commit_TakesTheAuthorFromTheEnvironment()
    {
        harness.RunCli("init", "--server", harness.ServerUrl, "--token", ScionServerFactory.TestToken);
        Write("file.txt", "content");

        var (exit, output) = WithEnvironment(
        [
            (ScionEnvironment.AuthorNameVariable, "Env Author"),
            (ScionEnvironment.AuthorEmailVariable, "env@example.com"),
        ], () => harness.RunCli("commit", "-m", "env author"));
        Assert.Equal(0, exit);

        var stored = await harness.GetJsonAsync($"/rpc/changesets/{CommittedId(output)}");
        Assert.Equal("Env Author", stored.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal("env@example.com", stored.GetProperty("author").GetProperty("email").GetString());
    }

    [Fact]
    public void Commit_WithoutAnAuthorAnywhere_FailsWithGuidance()
    {
        harness.RunCli("init", "--server", harness.ServerUrl, "--token", ScionServerFactory.TestToken);
        Write("file.txt", "content");

        var (exit, output) = harness.RunCli("commit", "-m", "authorless");
        Assert.Equal(1, exit);
        Assert.Contains("No author configured", output);
    }

    [Fact]
    public void Commit_TakesTheAuthorFromTheConfig()
    {
        harness.RunCli("init", "--server", harness.ServerUrl, "--token", ScionServerFactory.TestToken, "--name", "Config Author", "--email", "cfg@example.com");
        Write("file.txt", "content");

        var (exit, output) = harness.RunCli("commit", "-m", "config author");
        Assert.Equal(0, exit);
        Assert.Matches(@"committed [0-9a-f]{64}", output);
    }

    private void Write(string path, string content) => File.WriteAllText(Path.Combine(harness.RepoRoot, path), content);

    private static string CommittedId(string output)
    {
        var match = System.Text.RegularExpressions.Regex.Match(output, @"committed ([0-9a-f]{64})");
        Assert.True(match.Success, $"Expected a committed changeset id in the output, got: {output}");
        return match.Groups[1].Value;
    }

    /// <summary>Sets the variables, runs the action, restores the prior values.</summary>
    private static (int Exit, string Output) WithEnvironment((string Name, string? Value)[] variables, Func<(int Exit, string Output)> action)
    {
        var restore = variables.Select(variable => (variable.Name, Environment.GetEnvironmentVariable(variable.Name))).ToArray();
        try
        {
            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            return action();
        }
        finally
        {
            foreach (var (name, value) in restore)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    public void Dispose() => harness.Dispose();
}
