using Mutual.Core;
using Xunit;

namespace Mutual.Core.Tests;

public class SshLoginTests
{
    static readonly string[] Config =
    {
        "Host *", "    ServerAliveInterval 30",
        "Host nubzdoctor", "    HostName 25.81.93.91", "    User berub",
    };

    [Fact]
    public void NeverAllowsAPasswordPrompt()
    {
        var a = SshLogin.Args("25.81.93.91", null);
        Assert.Contains("PasswordAuthentication=no", a);
        Assert.Contains("KbdInteractiveAuthentication=no", a);
    }

    [Fact]
    public void UsesTheirNameAndKeepsTheTargetLast()
    {
        var a = SshLogin.Args("25.81.93.91", "berub");
        Assert.Equal(new[] { "-l", "berub", "--", "25.81.93.91" }, a.Skip(4));
    }

    [Theory]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("a;b")]
    [InlineData("a\"b")]
    [InlineData("")]
    [InlineData(null)]
    public void BadNamesAreDropped(string? name)
    {
        Assert.False(SshLogin.IsUsableName(name));
        Assert.DoesNotContain("-l", SshLogin.Args("1.2.3.4", name));
    }

    [Theory]
    [InlineData("berub")]
    [InlineData("Aiden")]
    [InlineData("Tyler Smith")]
    [InlineData("first.last-2")]
    public void RealNamesAreKept(string name) => Assert.True(SshLogin.IsUsableName(name));

    [Fact]
    public void FindsAnAliasByItsAddress() => Assert.Equal("nubzdoctor", SshLogin.FindAlias(Config, "Nubz", "25.81.93.91"));

    [Fact]
    public void FindsAnAliasByTheirName() => Assert.Equal("Nubz", SshLogin.FindAlias(new[] { "Host foo Nubz", "  HostName 9.9.9.9" }, "Nubz", "1.1.1.1"));

    [Fact]
    public void NoMatchMeansNoAlias() => Assert.Null(SshLogin.FindAlias(Config, "Nubz", "25.0.0.1"));
}
