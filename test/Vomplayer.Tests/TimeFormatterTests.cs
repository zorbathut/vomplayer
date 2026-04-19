using System.Globalization;
using System.Threading;
using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class TimeFormatterTests
{
    [Test]
    public void Zero()
    {
        Assert.That(TimeFormatter.Format(0), Is.EqualTo("00:00"));
    }

    [Test]
    public void SubMinute()
    {
        Assert.That(TimeFormatter.Format(7), Is.EqualTo("00:07"));
    }

    [Test]
    public void MinutesAndSeconds()
    {
        Assert.That(TimeFormatter.Format(125), Is.EqualTo("02:05"));
    }

    [Test]
    public void JustUnderOneHour()
    {
        Assert.That(TimeFormatter.Format(3599), Is.EqualTo("59:59"));
    }

    [Test]
    public void OneHourExactly()
    {
        Assert.That(TimeFormatter.Format(3600), Is.EqualTo("1:00:00"));
    }

    [Test]
    public void HoursMinutesSeconds()
    {
        Assert.That(TimeFormatter.Format(3 * 3600 + 25 * 60 + 9), Is.EqualTo("3:25:09"));
    }

    [Test]
    public void DoubleDigitHours()
    {
        Assert.That(TimeFormatter.Format(12 * 3600 + 5 * 60), Is.EqualTo("12:05:00"));
    }

    [Test]
    public void FractionalSecondsTruncateToInteger()
    {
        // TimeSpan.FromSeconds rounds to ticks; at the seconds level we expect truncation.
        Assert.That(TimeFormatter.Format(61.9), Is.EqualTo("01:01"));
    }

    [Test]
    public void NegativeTreatedAsZero()
    {
        Assert.That(TimeFormatter.Format(-42), Is.EqualTo("00:00"));
    }

    [Test]
    public void NaNTreatedAsZero()
    {
        Assert.That(TimeFormatter.Format(double.NaN), Is.EqualTo("00:00"));
    }

    [Test]
    public void StableUnderNonInvariantCulture()
    {
        // Some locales format numbers with commas; make sure the formatter isn't accidentally
        // picking up culture-specific separators from TimeSpan/int formatting.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.That(TimeFormatter.Format(3 * 3600 + 25 * 60 + 9), Is.EqualTo("3:25:09"));
            Assert.That(TimeFormatter.Format(125), Is.EqualTo("02:05"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
