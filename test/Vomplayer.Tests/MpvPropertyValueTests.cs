using Vomplayer.Mpv;

namespace Vomplayer.Tests;

[TestFixture]
public class MpvPropertyValueTests
{
    [Test]
    public void DoubleRoundTrips()
    {
        var v = new MpvPropertyValue(42.5);
        Assert.That(v.AsDouble, Is.EqualTo(42.5));
        Assert.That(v.AsInt64, Is.Null);
        Assert.That(v.AsFlag, Is.Null);
        Assert.That(v.AsString, Is.Null);
    }

    [Test]
    public void Int64RoundTrips()
    {
        var v = new MpvPropertyValue(1234L);
        Assert.That(v.AsInt64, Is.EqualTo(1234L));
        Assert.That(v.AsDouble, Is.Null);
        Assert.That(v.AsFlag, Is.Null);
    }

    [Test]
    public void FlagTrueRoundTrips()
    {
        var v = new MpvPropertyValue(1);
        Assert.That(v.AsFlag, Is.True);
    }

    [Test]
    public void FlagFalseRoundTrips()
    {
        var v = new MpvPropertyValue(0);
        Assert.That(v.AsFlag, Is.False);
    }

    [Test]
    public void StringRoundTrips()
    {
        var v = new MpvPropertyValue("hello");
        Assert.That(v.AsString, Is.EqualTo("hello"));
        Assert.That(v.AsDouble, Is.Null);
    }

    [Test]
    public void NullRawAllNull()
    {
        var v = new MpvPropertyValue(null);
        Assert.That(v.AsDouble, Is.Null);
        Assert.That(v.AsInt64, Is.Null);
        Assert.That(v.AsFlag, Is.Null);
        Assert.That(v.AsString, Is.Null);
    }

    [Test]
    public void DoubleDoesNotCoerceToInt64()
    {
        // AsInt64 should only unwrap an actual long; it must not coerce from double.
        var v = new MpvPropertyValue(5.0);
        Assert.That(v.AsInt64, Is.Null);
    }
}
