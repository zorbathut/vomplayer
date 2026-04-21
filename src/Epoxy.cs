using System;
using System.Runtime.InteropServices;

namespace Vomplayer;

// GL proc-address resolution for mpv's render init params, plus glGetIntegerv for querying the FBO GTK4's GLArea binds before firing OnRender. libEGL.so.1 is the right eglGetProcAddress source on a Wayland GTK backend; libGL.so.1 carries the classic glGetIntegerv symbol.
internal static partial class Epoxy
{
    private const string GlLib = "libGL.so.1";
    private const string EglLib = "libEGL.so.1";

    private const uint GL_DRAW_FRAMEBUFFER_BINDING = 0x8CA6;

    [LibraryImport(EglLib, EntryPoint = "eglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GetProcAddress(string name);

    [LibraryImport(GlLib, EntryPoint = "glGetIntegerv")]
    private static partial void GlGetIntegerv(uint pname, out int data);

    public static int GetCurrentDrawFbo()
    {
        GlGetIntegerv(GL_DRAW_FRAMEBUFFER_BINDING, out var fbo);
        return fbo;
    }
}
