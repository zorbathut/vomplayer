using System.Reflection;
using Vomplayer.Util;

namespace Vomplayer.Tests;

[TestFixture]
public class GtkDialogErrorTests
{
    [Test]
    public void GExceptionErrorHandleReflectionContractHolds()
    {
        // GtkDialogError.IsDismissed reads GException's private _errorHandle field and calls GetDomain/GetCode on it. If a GirCore upgrade renames any of these, IsDismissed silently degrades to message-substring matching — this test makes that degradation loud at upgrade time instead.
        var field = typeof(GLib.GException).GetField("_errorHandle", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(field, Is.Not.Null, "GirCore renamed GException._errorHandle");
        Assert.That(field!.FieldType, Is.EqualTo(typeof(GLib.Internal.ErrorHandle)));
        Assert.That(typeof(GLib.Internal.ErrorHandle).GetMethod("GetDomain"), Is.Not.Null);
        Assert.That(typeof(GLib.Internal.ErrorHandle).GetMethod("GetCode"), Is.Not.Null);
    }
}
