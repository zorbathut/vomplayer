using System.Collections.Generic;
using Vomplayer.UserData;

namespace Vomplayer.Tests;

[TestFixture]
public class ThemeModeTests
{
    [TestCase("auto", ThemeMode.Auto)]
    [TestCase("light", ThemeMode.Light)]
    [TestCase("dark", ThemeMode.Dark)]
    [TestCase("AUTO", ThemeMode.Auto)]
    [TestCase("Light", ThemeMode.Light)]
    [TestCase("  Dark  ", ThemeMode.Dark)]
    public void ParseRecognizesValuesCaseAndWhitespaceInsensitively(string raw, ThemeMode expected)
    {
        var warnings = new List<string>();
        var mode = ThemeModeParser.Parse(raw, warnings.Add);
        Assert.That(mode, Is.EqualTo(expected));
        Assert.That(warnings, Is.Empty);
    }

    [TestCase("bogus")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void ParseFallsBackToAutoAndWarnsOnUnrecognized(string? raw)
    {
        var warnings = new List<string>();
        var mode = ThemeModeParser.Parse(raw, warnings.Add);
        Assert.That(mode, Is.EqualTo(ThemeMode.Auto));
        Assert.That(warnings, Has.Count.EqualTo(1));
    }

    [TestCase(ThemeMode.Auto, "auto")]
    [TestCase(ThemeMode.Light, "light")]
    [TestCase(ThemeMode.Dark, "dark")]
    public void ToConfigStringMatchesParse(ThemeMode mode, string expected)
    {
        Assert.That(ThemeModeParser.ToConfigString(mode), Is.EqualTo(expected));
        // Round-trips back through Parse.
        var warnings = new List<string>();
        Assert.That(ThemeModeParser.Parse(expected, warnings.Add), Is.EqualTo(mode));
        Assert.That(warnings, Is.Empty);
    }

    [TestCase(ThemeMode.Light, true, false)]
    [TestCase(ThemeMode.Light, false, false)]
    [TestCase(ThemeMode.Dark, true, true)]
    [TestCase(ThemeMode.Dark, false, true)]
    [TestCase(ThemeMode.Auto, true, true)]
    [TestCase(ThemeMode.Auto, false, false)]
    public void ResolvePreferDarkForcesLightDarkAndPassesThroughAuto(ThemeMode mode, bool systemDefault, bool expected)
    {
        Assert.That(ThemeModeParser.ResolvePreferDark(mode, systemDefault), Is.EqualTo(expected));
    }
}
