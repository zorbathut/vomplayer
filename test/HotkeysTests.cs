using System.Collections.Generic;
using Vomplayer.UserData;

namespace Vomplayer.Tests;

[TestFixture]
public class HotkeysTests
{
    [Test]
    public void TriggerParsesPlainKey()
    {
        var t = Trigger.TryParse("f");
        Assert.That(t, Is.InstanceOf<Trigger.Key>());
        var k = (Trigger.Key)t!;
        Assert.That(k.Keyval, Is.EqualTo((uint)Gdk.Constants.KEY_f));
        Assert.That(k.Modifiers, Is.EqualTo((Gdk.ModifierType)0));
    }

    [Test]
    public void TriggerParsesModifiedKey()
    {
        var t = Trigger.TryParse("<Primary>O");
        Assert.That(t, Is.InstanceOf<Trigger.Key>());
        var k = (Trigger.Key)t!;
        // Primary maps to Control on Linux; the modifier bit should be set.
        Assert.That(k.Modifiers.HasFlag(Gdk.ModifierType.ControlMask), Is.True);
    }

    [Test]
    public void TriggerRejectsGarbage()
    {
        Assert.That(Trigger.TryParse("not-a-real-key"), Is.Null);
        Assert.That(Trigger.TryParse(""), Is.Null);
        Assert.That(Trigger.TryParse("   "), Is.Null);
    }

    [Test]
    public void TriggerParsesMouseClick()
    {
        var t = Trigger.TryParse("MouseClick1");
        Assert.That(t, Is.InstanceOf<Trigger.MouseClick>());
        var m = (Trigger.MouseClick)t!;
        Assert.That(m.Button, Is.EqualTo(1u));
        Assert.That(m.ClickCount, Is.EqualTo(1));
    }

    [Test]
    public void TriggerParsesMouseDoubleClick()
    {
        var t = Trigger.TryParse("MouseDoubleClick3");
        Assert.That(t, Is.InstanceOf<Trigger.MouseClick>());
        var m = (Trigger.MouseClick)t!;
        Assert.That(m.Button, Is.EqualTo(3u));
        Assert.That(m.ClickCount, Is.EqualTo(2));
    }

    [Test]
    public void TriggerRejectsMalformedMouse()
    {
        Assert.That(Trigger.TryParse("MouseClick"), Is.Null);
        Assert.That(Trigger.TryParse("MouseClick0"), Is.Null);
        Assert.That(Trigger.TryParse("MouseClickX"), Is.Null);
    }

    [Test]
    public void KeyTriggerFormatRoundTrips()
    {
        // gtk_accelerator_parse + gtk_accelerator_name are GTK's own canonical forms; whatever the parser accepts must format-back to a parseable string. Spot-check several inputs.
        foreach (var s in new[] { "f", "F11", "<Primary>O", "<Shift>F", "space", "Escape" })
        {
            var t = Trigger.TryParse(s);
            Assert.That(t, Is.Not.Null, $"failed to parse {s}");
            var formatted = t!.Format();
            var roundTripped = Trigger.TryParse(formatted);
            Assert.That(roundTripped, Is.EqualTo(t), $"round-trip mismatch for {s} (formatted as {formatted})");
        }
    }

    [Test]
    public void MouseTriggerFormatRoundTrips()
    {
        foreach (var s in new[] { "MouseClick1", "MouseClick2", "MouseDoubleClick1", "MouseDoubleClick3" })
        {
            var t = Trigger.TryParse(s);
            Assert.That(t, Is.Not.Null);
            Assert.That(t!.Format(), Is.EqualTo(s));
        }
    }

    [Test]
    public void HotkeyMapDefaultsCoverAllActions()
    {
        var map = HotkeyMap.Default();
        // Every action in the enum must produce a list (possibly empty) so the dialog can render a row for it without null checks.
        foreach (HotkeyAction action in System.Enum.GetValues<HotkeyAction>())
        {
            Assert.That(map.Get(action), Is.Not.Null);
        }
        Assert.That(map.Get(HotkeyAction.PlayPause), Has.Count.EqualTo(2));
        Assert.That(map.Get(HotkeyAction.ToggleFullscreen), Has.Count.EqualTo(4));
    }

    [TestCase("Left", HotkeyAction.SeekBack5)]
    [TestCase("Right", HotkeyAction.SeekForward5)]
    [TestCase("j", HotkeyAction.SeekBack10)]
    [TestCase("l", HotkeyAction.SeekForward10)]
    [TestCase("comma", HotkeyAction.FrameStepBack)]
    [TestCase("period", HotkeyAction.FrameStepForward)]
    [TestCase("Home", HotkeyAction.SeekStart)]
    [TestCase("End", HotkeyAction.SeekEnd)]
    [TestCase("<Primary>Left", HotkeyAction.ChapterPrev)]
    [TestCase("<Primary>Right", HotkeyAction.ChapterNext)]
    [TestCase("k", HotkeyAction.PlayPause)]
    public void DefaultBindingResolvesToExpectedAction(string accelerator, HotkeyAction expected)
    {
        var map = HotkeyMap.Default();
        var trigger = Trigger.TryParse(accelerator);
        Assert.That(trigger, Is.Not.Null, $"failed to parse {accelerator}");
        Assert.That(map.Lookup(trigger!), Is.EqualTo(expected));
    }

    [Test]
    public void TomlRoundTripPreservesNewActionsBindings()
    {
        var src = HotkeyMap.Default();
        var roundTripped = HotkeyMap.FromTomlForm(src.ToTomlForm(), _ => { });
        foreach (HotkeyAction action in System.Enum.GetValues<HotkeyAction>())
        {
            Assert.That(roundTripped.Get(action), Is.EqualTo(src.Get(action)), $"mismatch for {action}");
        }
    }

    [Test]
    public void LookupReturnsBoundAction()
    {
        var map = HotkeyMap.Default();
        var t = Trigger.TryParse("f");
        Assert.That(map.Lookup(t!), Is.EqualTo(HotkeyAction.ToggleFullscreen));
        var space = Trigger.TryParse("space");
        Assert.That(map.Lookup(space!), Is.EqualTo(HotkeyAction.PlayPause));
        var dbl = Trigger.TryParse("MouseDoubleClick1");
        Assert.That(map.Lookup(dbl!), Is.EqualTo(HotkeyAction.ToggleFullscreen));
    }

    [Test]
    public void LookupReturnsNullForUnboundTrigger()
    {
        var map = HotkeyMap.Default();
        var t = Trigger.TryParse("<Primary><Shift>X");
        Assert.That(map.Lookup(t!), Is.Null);
    }

    [Test]
    public void FromTomlFormDropsUnparseableTriggersAndWarns()
    {
        var warnings = new List<string>();
        var map = HotkeyMap.FromTomlForm(new Dictionary<string, List<string>>
        {
            ["play_pause"] = new() { "p", "garbage-key", "<Primary>X" },
        }, warnings.Add);
        Assert.That(warnings, Has.Count.EqualTo(1));
        var pp = map.Get(HotkeyAction.PlayPause);
        Assert.That(pp, Has.Count.EqualTo(2));
    }

    [Test]
    public void FromTomlFormUnknownActionWarnsAndContinues()
    {
        var warnings = new List<string>();
        var map = HotkeyMap.FromTomlForm(new Dictionary<string, List<string>>
        {
            ["wat_is_this"] = new() { "f" },
            ["play_pause"] = new() { "p" },
        }, warnings.Add);
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(map.Get(HotkeyAction.PlayPause), Has.Count.EqualTo(1));
    }

    [Test]
    public void FromTomlFormPreservesDefaultsForOmittedActions()
    {
        var map = HotkeyMap.FromTomlForm(new Dictionary<string, List<string>>
        {
            ["play_pause"] = new() { "p" },
        }, _ => { });
        // An unspecified action keeps its default — we only override what the file mentions.
        Assert.That(map.Get(HotkeyAction.Open), Has.Count.EqualTo(1));
    }

    // The next four tests pin live-event-vs-binding semantics — without these, the canonicalization that makes "<Shift>F" / Shift+f / CapsLock+f all match the same binding can silently regress.

    [Test]
    public void LiveShiftLetterMatchesShiftAcceleratorBinding()
    {
        var map = HotkeyMap.Default();
        // The default ToggleFullscreen contains "<Shift>F" — built via Trigger.MakeKey(KEY_F, ShiftMask). A live Shift+f event also delivers KEY_F + ShiftMask; canonicalization to lowercase keyval must keep the two equal.
        var live = Trigger.MakeKey((uint)Gdk.Constants.KEY_F, Gdk.ModifierType.ShiftMask);
        Assert.That(map.Lookup(live), Is.EqualTo(HotkeyAction.ToggleFullscreen));
    }

    [Test]
    public void LiveCapsLockLetterMatchesPlainBinding()
    {
        var map = HotkeyMap.Default();
        // f-with-CapsLock-on is delivered as (KEY_F, LockMask). Default mask strips LockMask, lowercase normalize gives KEY_f. Should match the plain "f" binding.
        var live = Trigger.MakeKey((uint)Gdk.Constants.KEY_F, Gdk.ModifierType.LockMask);
        Assert.That(map.Lookup(live), Is.EqualTo(HotkeyAction.ToggleFullscreen));
    }

    [Test]
    public void LivePlainLetterMatchesPlainBinding()
    {
        var map = HotkeyMap.Default();
        var live = Trigger.MakeKey((uint)Gdk.Constants.KEY_f, 0);
        Assert.That(map.Lookup(live), Is.EqualTo(HotkeyAction.ToggleFullscreen));
    }

    [Test]
    public void LiveF11MatchesF11Binding()
    {
        var map = HotkeyMap.Default();
        var live = Trigger.MakeKey((uint)Gdk.Constants.KEY_F11, 0);
        Assert.That(map.Lookup(live), Is.EqualTo(HotkeyAction.ToggleFullscreen));
    }

    [Test]
    public void LiveCtrlOMatchesParsedPrimaryO()
    {
        var map = HotkeyMap.Default();
        var live = Trigger.MakeKey((uint)Gdk.Constants.KEY_O, Gdk.ModifierType.ControlMask);
        Assert.That(map.Lookup(live), Is.EqualTo(HotkeyAction.Open));
        // Lowercase form of the live event matches too — they should canonicalize identically.
        var liveLower = Trigger.MakeKey((uint)Gdk.Constants.KEY_o, Gdk.ModifierType.ControlMask);
        Assert.That(map.Lookup(liveLower), Is.EqualTo(HotkeyAction.Open));
    }

    [Test]
    public void BindMovesTriggerFromOtherAction()
    {
        var map = HotkeyMap.Default();
        var ctrlQ = Trigger.MakeKey((uint)Gdk.Constants.KEY_Q, Gdk.ModifierType.ControlMask);
        // Originally bound to Quit.
        Assert.That(map.Lookup(ctrlQ), Is.EqualTo(HotkeyAction.Quit));

        var previousOwner = map.Bind(HotkeyAction.Open, ctrlQ);
        Assert.That(previousOwner, Is.EqualTo(HotkeyAction.Quit));
        Assert.That(map.Lookup(ctrlQ), Is.EqualTo(HotkeyAction.Open));
        // Quit should no longer have it.
        Assert.That(map.Get(HotkeyAction.Quit), Does.Not.Contain(ctrlQ));
    }

    [Test]
    public void BindReturnsNullWhenTriggerIsFresh()
    {
        var map = HotkeyMap.Default();
        var unused = Trigger.MakeKey((uint)Gdk.Constants.KEY_z, Gdk.ModifierType.ControlMask | Gdk.ModifierType.AltMask);
        var previousOwner = map.Bind(HotkeyAction.Open, unused);
        Assert.That(previousOwner, Is.Null);
    }

    [Test]
    public void CloneIsIndependent()
    {
        var a = HotkeyMap.Default();
        var b = a.Clone();
        b.Set(HotkeyAction.PlayPause, System.Array.Empty<Trigger>());
        Assert.That(a.Get(HotkeyAction.PlayPause), Is.Not.Empty);
        Assert.That(b.Get(HotkeyAction.PlayPause), Is.Empty);
    }
}
