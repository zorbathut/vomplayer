namespace Vomplayer.Tests;

// Covers the pure classification/formatting half of the g_printerr hook. The native trampolines are deliberately untested: Install() installs both hooks together, and the writer half is one-shot process-global state (g_log_set_writer_func aborts on a second call), so exercising it here would poison every other fixture in this process. The write(2) plumbing is covered separately in LibCTests.
[TestFixture]
public class GLibLogDiagTests
{
    [Test]
    public void AssertionMessageProducesMarkerAndStack()
    {
        string message = "**\nGLib-GObject:ERROR:../glib/gobject/gsignal.c:4110:invalid_closure_notify: assertion failed: (handler != NULL)\n";
        string result = GLibLogDiag.BuildPrinterrAugmentation(message, 7, "FAKE STACK LINE");
        Assert.That(result, Does.Contain("[vomplayer] managed stack at GLib assertion (tid=7):"));
        Assert.That(result, Does.Contain("FAKE STACK LINE"));
    }

    [Test]
    public void PlainMessageProducesNoAugmentation()
    {
        string result = GLibLogDiag.BuildPrinterrAugmentation("plain printerr message\n", 7, "FAKE STACK LINE");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void AssertionPrefixMustBeAtStart()
    {
        string result = GLibLogDiag.BuildPrinterrAugmentation("noise **\nERROR:x.c:1:f: assertion failed: (x)\n", 7, "FAKE STACK LINE");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void BareDoubleStarWithoutNewlineProducesNoAugmentation()
    {
        string result = GLibLogDiag.BuildPrinterrAugmentation("**", 7, "FAKE STACK LINE");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void AugmentationEndsWithNewline()
    {
        string result = GLibLogDiag.BuildPrinterrAugmentation("**\nERROR:x.c:1:f: assertion failed: (x)\n", 3, "STACK");
        Assert.That(result, Does.EndWith("\n"));
    }
}
