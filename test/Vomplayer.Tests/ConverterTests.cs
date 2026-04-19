using System;
using System.Globalization;
using Avalonia.Data;
using Vomplayer.Converters;

namespace Vomplayer.Tests;

[TestFixture]
public class TimeFormatConverterTests
{
    private static readonly TimeFormatConverter Converter = new();

    [Test]
    public void TimeSpanFormatsAsMinutesSeconds()
    {
        var result = Converter.Convert(TimeSpan.FromSeconds(125), typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.EqualTo("02:05"));
    }

    [Test]
    public void DoubleFormatsAsMinutesSeconds()
    {
        var result = Converter.Convert(42.0, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.EqualTo("00:42"));
    }

    [Test]
    public void UnsupportedReturnsDoNothing()
    {
        var result = Converter.Convert("garbage", typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.SameAs(BindingOperations.DoNothing));
    }

    [Test]
    public void NullReturnsDoNothing()
    {
        var result = Converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.SameAs(BindingOperations.DoNothing));
    }
}

[TestFixture]
public class PlayPauseLabelConverterTests
{
    private static readonly PlayPauseLabelConverter Converter = new();

    [Test]
    public void TrueMeansPaused_ReturnsPlay()
    {
        var result = Converter.Convert(true, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.EqualTo("Play"));
    }

    [Test]
    public void FalseMeansPlaying_ReturnsPause()
    {
        var result = Converter.Convert(false, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.EqualTo("Pause"));
    }

    [Test]
    public void NonBoolReturnsDoNothing()
    {
        var result = Converter.Convert(42, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.SameAs(BindingOperations.DoNothing));
    }
}
