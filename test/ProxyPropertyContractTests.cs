using System;
using System.Linq;
using System.Reflection;
using Vomplayer.ViewModels;

namespace Vomplayer.Tests;

// The view's stringly PropertyChanged dispatch rests on two hand-maintained couplings: every ViewModelMain proxy property must exist under the same name on VideoContext (the coordinator re-fires context PropertyChanged names verbatim), and ProxyPropertyNames must list exactly the proxies (it drives the re-fire-everything on selection swaps). Neither is compiler-checked; this pins both so drift fails a test instead of silently un-binding a widget.
[TestFixture]
public class ProxyPropertyContractTests
{
    private static readonly string[] ProxyNames = GetProxyPropertyNames();

    private static string[] GetProxyPropertyNames()
    {
        var field = typeof(ViewModelMain).GetField("ProxyPropertyNames", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(field, Is.Not.Null, "ViewModelMain.ProxyPropertyNames was renamed — update this contract test alongside it");
        return (string[])field!.GetValue(null)!;
    }

    [Test]
    public void EveryProxyNameExistsOnBothViewModelAndVideoContext()
    {
        foreach (var name in ProxyNames)
        {
            Assert.That(typeof(ViewModelMain).GetProperty(name), Is.Not.Null, $"ProxyPropertyNames lists '{name}' but ViewModelMain has no such public property");
            Assert.That(typeof(VideoContext).GetProperty(name), Is.Not.Null, $"ProxyPropertyNames lists '{name}' but VideoContext has no matching property — the verbatim re-fire contract is broken");
        }
    }

    [Test]
    public void ProxyNamesAreDistinct()
    {
        Assert.That(ProxyNames, Is.Unique);
    }

    [Test]
    public void ProxyTypesMatchBetweenViewModelAndVideoContext()
    {
        foreach (var name in ProxyNames)
        {
            var vmType = typeof(ViewModelMain).GetProperty(name)!.PropertyType;
            var ctxType = typeof(VideoContext).GetProperty(name)!.PropertyType;
            Assert.That(vmType, Is.EqualTo(ctxType), $"proxy '{name}' has type {vmType} on the VM but {ctxType} on VideoContext — the view would read a different shape after a selection swap");
        }
    }
}
