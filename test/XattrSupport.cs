using System.Runtime.InteropServices;
using System.Text;

namespace Vomplayer.Tests;

// Real setxattr(2) for tests that need a user attribute on a temp file (the document portal's host-path xattr, for instance). A filesystem without user xattr support (tmpfs before Linux 6.6, some CI sandboxes) ignores the test rather than failing it; any other errno is a real failure and fails loudly with the number.
internal static partial class XattrSupport
{
    private const int ENOTSUP = 95;

    [LibraryImport("libc.so.6", EntryPoint = "setxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetXattr(string path, string name, byte[] value, nuint size, int flags);

    public static void SetUserXattrOrIgnore(string path, string name, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (SetXattr(path, name, bytes, (nuint)bytes.Length, 0) == 0)
        {
            return;
        }
        int errno = Marshal.GetLastPInvokeError();
        if (errno == ENOTSUP)
        {
            Assert.Ignore("temp filesystem does not support user xattrs");
        }
        Assert.Fail($"setxattr({path}, {name}) failed with errno {errno}");
    }
}
