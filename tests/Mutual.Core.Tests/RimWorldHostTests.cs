using Mutual.Core;
using Xunit;

namespace Mutual.Core.Tests;

public class RimWorldHostTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("mutual-rimhost").FullName;
    string Steam => Path.Combine(dir, "steam");
    string Config => Path.Combine(dir, "ModsConfig.xml");

    public void Dispose() => Directory.Delete(dir, true);

    void InstallIn(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_294100.acf"), "x");
    }

    void Mods(params string[] ids) =>
        File.WriteAllText(Config, "<ModsConfigData><activeMods>" + string.Concat(ids.Select(i => "<li>" + i + "</li>")) + "</activeMods></ModsConfigData>");

    [Fact]
    public void NoSteamMeansNoGame() => Assert.Equal(HostProblem.NoGame, RimWorldHost.Check(null, Config, false));

    [Fact]
    public void SteamWithoutTheGameMeansNoGame()
    {
        Directory.CreateDirectory(Path.Combine(Steam, "steamapps"));
        Assert.Equal(HostProblem.NoGame, RimWorldHost.Check(Steam, Config, false));
    }

    [Fact]
    public void FindsTheGameInAnotherLibrary()
    {
        var other = Path.Combine(dir, "other lib");
        InstallIn(other);
        Directory.CreateDirectory(Path.Combine(Steam, "steamapps"));
        File.WriteAllText(Path.Combine(Steam, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\" { \"1\" { \"path\"\t\t\"" + other.Replace("\\", "\\\\") + "\" } }");
        Mods("aiden.splitcolony");
        Assert.Equal(HostProblem.None, RimWorldHost.Check(Steam, Config, false));
    }

    [Fact]
    public void GameThatsAlreadyRunningCountsEvenWithoutSteam()
    {
        Mods("aiden.splitcolony");
        Assert.Equal(HostProblem.None, RimWorldHost.Check(null, Config, true));
    }

    [Fact]
    public void ModMissingFromTheListIsNoMod()
    {
        InstallIn(Steam);
        Mods("ludeon.rimworld", "brrainz.harmony");
        Assert.Equal(HostProblem.NoMod, RimWorldHost.Check(Steam, Config, false));
    }

    [Theory]
    [InlineData("aiden.splitcolony")]
    [InlineData("Aiden.SplitColony")]
    [InlineData("aiden.splitcolony_steam")]
    public void ModOnTheListIsFine(string id)
    {
        InstallIn(Steam);
        Mods("ludeon.rimworld", id);
        Assert.Equal(HostProblem.None, RimWorldHost.Check(Steam, Config, false));
    }

    [Fact]
    public void ConfigItCantReadDoesntBlock()
    {
        InstallIn(Steam);
        Assert.Equal(HostProblem.None, RimWorldHost.Check(Steam, Config, false));
        File.WriteAllText(Config, "not xml <<<");
        Assert.Equal(HostProblem.None, RimWorldHost.Check(Steam, Config, false));
    }
}
