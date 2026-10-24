namespace StressClient.Tests;

/// <summary>Command-line parsing: the password can come from STRESSCLIENT_PASSWORD (the web admin Tools page passes it that way).</summary>
public class OptionsTests
{
    [Fact]
    public void Password_FromEnvironment_ArgumentWins()
    {
        var before = Environment.GetEnvironmentVariable(Options.PasswordEnv);
        try
        {
            Environment.SetEnvironmentVariable(Options.PasswordEnv, "EnvPass12");
            var o = Options.Parse(new[] { "--bots", "2" }, out var error);
            Assert.Null(error);
            Assert.Equal("EnvPass12", o.Password);

            o = Options.Parse(new[] { "--password", "ArgPass12" }, out error);
            Assert.Null(error);
            Assert.Equal("ArgPass12", o.Password);

            Environment.SetEnvironmentVariable(Options.PasswordEnv, "bad pw");
            Assert.Null(Options.Parse(Array.Empty<string>(), out error));
            Assert.Contains("--password", error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Options.PasswordEnv, before);
        }
    }
}
