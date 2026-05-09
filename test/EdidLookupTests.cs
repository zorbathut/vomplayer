using System.IO;
using System.Runtime.InteropServices;
using Vomplayer.Wayland;

namespace Vomplayer.Tests;

[TestFixture]
public class EdidLookupTests
{
    [Test]
    public void EmptyConnectorNameReturnsNull()
    {
        var lookup = new EdidLookup();
        Assert.That(lookup.TryReadEdid(""), Is.Null);
        Assert.That(lookup.TryReadEdid(null!), Is.Null);
    }

    [Test]
    public void NonexistentConnectorReturnsNull()
    {
        var lookup = new EdidLookup();
        // Implausible connector name that will never match a real /sys/class/drm/card*-* path.
        Assert.That(lookup.TryReadEdid("DEFINITELY-NOT-A-REAL-CONNECTOR-XYZ"), Is.Null);
    }

    [Test]
    public void PopulatedEdidIsReadableOnLinuxIfPresent()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || !Directory.Exists("/sys/class/drm"))
        {
            Assert.Ignore("Linux/DRM-only");
            return;
        }
        // Find any populated connector by scanning real sysfs ourselves; if none, test environment has no connected display, skip.
        string? populatedConnector = null;
        foreach (var dir in Directory.GetDirectories("/sys/class/drm", "card*-*"))
        {
            string edidPath = Path.Combine(dir, "edid");
            if (!File.Exists(edidPath))
            {
                continue;
            }
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(edidPath);
            }
            catch
            {
                continue;
            }
            if (bytes.Length >= 128)
            {
                // dirname like "card0-HDMI-A-1" → strip "cardN-".
                string dirName = Path.GetFileName(dir);
                int dash = dirName.IndexOf('-');
                if (dash >= 0)
                {
                    populatedConnector = dirName.Substring(dash + 1);
                    break;
                }
            }
        }
        if (populatedConnector == null)
        {
            Assert.Ignore("No connected display with populated EDID on this host");
            return;
        }
        var lookup = new EdidLookup();
        var bytes2 = lookup.TryReadEdid(populatedConnector);
        Assert.That(bytes2, Is.Not.Null);
        Assert.That(bytes2!.Length, Is.GreaterThanOrEqualTo(128));
        // Header signature anchor — if this read landed somewhere other than EDID, the signature won't match.
        Assert.That(bytes2[0], Is.EqualTo(0x00));
        Assert.That(bytes2[1], Is.EqualTo(0xFF));
        Assert.That(bytes2[7], Is.EqualTo(0x00));
    }
}
