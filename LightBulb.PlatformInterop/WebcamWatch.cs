using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LightBulb.PlatformInterop.Internal;

namespace LightBulb.PlatformInterop;

[Flags]
public enum WebcamWatchEvents
{
    None = 0,
    Opened = 1,
    Closed = 2,
}

// Watches a camera's device node for programs opening and closing it, through inotify, which
// reports every open of the node whoever makes it. It says nothing about who, so what it reports
// is a cue to look, and to let the camera go before anyone has to wait for it.
[SupportedOSPlatform("linux")]
public sealed class WebcamWatch : IDisposable
{
    private readonly int _fd;
    private readonly byte[] _buffer = new byte[4096];
    private bool _isDisposed;

    private WebcamWatch(int fd) => _fd = fd;

    internal int Fd => _fd;

    public static WebcamWatch? TryCreate(string devicePath)
    {
        var fd = NativeMethods.inotify_init1(Inotify.NonBlocking | Inotify.CloseOnExec);
        if (fd < 0)
        {
            Debug.WriteLine($"Failed to create a watch: error {Marshal.GetLastPInvokeError()}.");
            return null;
        }

        var watchDescriptor = NativeMethods.inotify_add_watch(
            fd,
            devicePath,
            Inotify.Open | Inotify.CloseWrite | Inotify.CloseNoWrite
        );

        if (watchDescriptor < 0)
        {
            Debug.WriteLine(
                $"Failed to watch '{devicePath}': error {Marshal.GetLastPInvokeError()}."
            );
            NativeMethods.close(fd);
            return null;
        }

        return new WebcamWatch(fd);
    }

    // Takes in everything that has happened since the last call
    public WebcamWatchEvents Drain()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var events = WebcamWatchEvents.None;

        while (true)
        {
            var read = NativeMethods.read(_fd, _buffer, (nuint)_buffer.Length);
            if (read < 0)
            {
                if (Marshal.GetLastPInvokeError() == Webcam.Interrupted)
                    continue;

                // Nothing more to read, or the watch is gone
                break;
            }

            if (read == 0)
                break;

            for (var offset = 0; offset + Inotify.EventHeaderSize <= read; )
            {
                var mask = BitConverter.ToUInt32(_buffer, offset + Inotify.MaskOffset);
                var nameLength = BitConverter.ToInt32(_buffer, offset + Inotify.NameLengthOffset);

                if ((mask & Inotify.Open) != 0)
                    events |= WebcamWatchEvents.Opened;

                if ((mask & (Inotify.CloseWrite | Inotify.CloseNoWrite)) != 0)
                    events |= WebcamWatchEvents.Closed;

                offset += Inotify.EventHeaderSize + nameLength;
            }
        }

        return events;
    }

    // Waits for something to happen, at most for the time given
    public WebcamWatchEvents Wait(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
                return WebcamWatchEvents.None;

            var pollFd = new PollFd { Fd = _fd, Events = Webcam.PollIn };
            var polled = NativeMethods.poll(
                ref pollFd,
                1,
                (int)Math.Ceiling(remaining.TotalMilliseconds)
            );

            if (polled < 0 && Marshal.GetLastPInvokeError() == Webcam.Interrupted)
                continue;

            return polled > 0 ? Drain() : WebcamWatchEvents.None;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        NativeMethods.close(_fd);
    }
}
