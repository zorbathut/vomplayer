using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Vomplayer.Wayland;

// Abstraction so WaylandOutputRegistry can be unit-tested without coupling to /sys/class/drm. Default impl is EdidLookup; tests inject a fake.
public interface IEdidSource
{
    byte[]? TryReadEdid(string connectorName);
}

// Reads the EDID base block (and any extension blocks) from /sys/class/drm/card*-<connector>/edid for a given Wayland output connector name. Multi-GPU systems: multiple cards can host a connector with the same suffix (card0-DP-2 / card1-DP-2); the first one with a populated edid file (≥128 bytes once read) wins. Linux-only — non-Linux returns null unconditionally and EdidParser handles the null upstream.
//
// Why we read the whole file rather than checking FileInfo.Length first: sysfs reports edid as a 0-byte file via stat(2). The actual byte count is only known after the kernel populates the buffer in response to a read syscall, so File.ReadAllBytes is the cheapest way to "is this connector connected and is its EDID readable?".
public sealed class EdidLookup : IEdidSource
{
    private const string DrmRoot = "/sys/class/drm";
    private const int MinEdidBytes = 128;
    // DRM connector names are alphanumeric + dash by KMS convention (e.g. "HDMI-A-1", "DP-2", "eDP-1"). Validate before passing to GetDirectories so a buggy compositor advertising "*" or "../badpath" can't widen the glob beyond the connector's directory.
    private static readonly Regex ConnectorNamePattern = new("^[A-Za-z0-9-]+$", RegexOptions.Compiled);

    public byte[]? TryReadEdid(string connectorName)
    {
        if (string.IsNullOrEmpty(connectorName))
        {
            return null;
        }
        if (!ConnectorNamePattern.IsMatch(connectorName))
        {
            return null;
        }
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }
        if (!Directory.Exists(DrmRoot))
        {
            return null;
        }
        string[] candidates;
        try
        {
            candidates = Directory.GetDirectories(DrmRoot, "card*-" + connectorName);
        }
        catch (Exception)
        {
            return null;
        }
        foreach (var dir in candidates)
        {
            byte[]? bytes = TryReadFile(Path.Combine(dir, "edid"));
            if (bytes != null && bytes.Length >= MinEdidBytes)
            {
                return bytes;
            }
        }
        return null;
    }

    private static byte[]? TryReadFile(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
