using System.Runtime.InteropServices;

namespace LightBulb.PlatformInterop.Internal;

internal static partial class NativeMethods
{
    // The runtime maps this name to the C library the process already runs on
    private const string Libc = "libc";

    [DllImport(Libc, SetLastError = true)]
    public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport(Libc, SetLastError = true)]
    public static extern int close(int fd);

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, nuint request, ref V4l2Capability argument);

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, nuint request, ref V4l2Format argument);

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, nuint request, ref V4l2RequestBuffers argument);

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, nuint request, ref V4l2Buffer argument);

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, nuint request, ref V4l2Control argument);

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, nuint request, ref V4l2QueryControl argument);

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, nuint request, ref int argument);

    [DllImport(Libc, SetLastError = true)]
    public static extern nint mmap(
        nint address,
        nuint length,
        int protection,
        int flags,
        int fd,
        long offset
    );

    [DllImport(Libc, SetLastError = true)]
    public static extern int munmap(nint address, nuint length);

    [DllImport(Libc, SetLastError = true)]
    public static extern int poll(ref PollFd fds, nuint count, int timeout);
}

internal static partial class NativeMethods
{
    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, nuint request, ref V4l2StreamParm argument);

    [DllImport(Libc, SetLastError = true)]
    public static extern int poll([In, Out] PollFd[] fds, nuint count, int timeout);

    [DllImport(Libc, SetLastError = true)]
    public static extern nint read(int fd, byte[] buffer, nuint count);

    [DllImport(Libc, SetLastError = true)]
    public static extern int inotify_init1(int flags);

    [DllImport(Libc, SetLastError = true)]
    public static extern int inotify_add_watch(
        int fd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mask
    );
}
