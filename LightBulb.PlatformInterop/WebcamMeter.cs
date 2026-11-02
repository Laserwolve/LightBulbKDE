using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using LightBulb.PlatformInterop.Internal;

namespace LightBulb.PlatformInterop;

public enum WebcamMeterResult
{
    // A frame was read
    Reading,

    // No frame came within the time given
    Timeout,

    // The camera failed, and the meter is finished
    Failed,
}

// A camera held open and streaming in manual exposure, read frame by frame. The exposure moves
// toward the band whenever a frame falls outside it, as the camera's own metering would, and
// disposing the meter stops the stream, puts the camera's settings back and lets the camera go.
// While the meter is open, the camera streams to this program alone: a camera cannot be handed
// over the moment another program asks for it, because a camera asked for a format within a
// fraction of a second of a stream stopping can stop answering altogether, and then needs
// unplugging, so letting go is left to whoever holds the meter, at a moment of their choosing.
[SupportedOSPlatform("linux")]
public sealed class WebcamMeter : IDisposable
{
    private readonly string _devicePath;
    private readonly int _fd;
    private readonly List<(nint Address, nuint Length)> _buffers = [];
    private readonly long _openTimestamp = Stopwatch.GetTimestamp();

    private Webcam.LumaLayout _layout;
    private byte[] _frame = [];
    private Webcam.ControlRange _exposureRange;
    private bool _areBuffersRequested;
    private bool _isStreaming;
    private bool _areControlsChanged;
    private int _previousExposureMode;
    private int? _previousExposure;
    private int? _previousGain;
    private int _exposure;
    private int _settlingFrames;
    private long _lastFrameTimestamp;
    private bool _isDisposed;

    internal WebcamMeter(string devicePath, int fd)
    {
        _devicePath = devicePath;
        _fd = fd;
    }

    // The exposure the camera is set to, as its control counts it
    public int ExposureSetting => _exposure;

    // Whether the last frame was within the band, so that the exposure stayed where it was
    public bool IsExposureSettled { get; private set; }

    internal bool TrySetUp(int? exposureSetting, TimeSpan? frameInterval)
    {
        // A small frame is plenty for an average, and asks little of the camera and the bus. The
        // driver answers with the nearest format it has, and refuses while another program streams.
        var format = new V4l2Format
        {
            Type = V4l2.BufferTypeVideoCapture,
            Pixel = new V4l2PixelFormat
            {
                Width = Webcam.FrameWidth,
                Height = Webcam.FrameHeight,
                PixelFormat = V4l2.PixelFormatYuyv,
                Field = V4l2.FieldNone,
            },
        };

        if (!TryIoctl(V4l2.SetFormat, ref format, "set the frame format"))
            return false;

        if (Webcam.LumaLayout.TryGet(format.Pixel) is not { } layout)
        {
            Debug.WriteLine($"'{_devicePath}' offers no frame format with a readable brightness.");
            return false;
        }

        _layout = layout;
        _frame = new byte[layout.FrameSize];

        // A slow frame rate keeps a stream that stays open cheap. The driver answers with the
        // nearest rate the camera has, and a camera with only one keeps it.
        if (frameInterval is { } interval)
        {
            var parameters = new V4l2StreamParm
            {
                Type = V4l2.BufferTypeVideoCapture,
                TimePerFrameNumerator = (uint)interval.Ticks,
                TimePerFrameDenominator = (uint)TimeSpan.TicksPerSecond,
            };

            if (TryIoctl(V4l2.SetStreamParm, ref parameters, "set the frame rate"))
            {
                Debug.WriteLine(
                    $"'{_devicePath}' streams a frame every {parameters.TimePerFrameNumerator}/{parameters.TimePerFrameDenominator} s."
                );
            }
        }

        var request = new V4l2RequestBuffers
        {
            Count = Webcam.BufferCount,
            Type = V4l2.BufferTypeVideoCapture,
            Memory = V4l2.MemoryMmap,
        };

        if (!TryIoctl(V4l2.RequestBuffers, ref request, "request frame buffers"))
            return false;

        _areBuffersRequested = true;
        if (request.Count == 0)
            return false;

        for (var index = 0u; index < request.Count; index++)
        {
            var buffer = new V4l2Buffer
            {
                Index = index,
                Type = V4l2.BufferTypeVideoCapture,
                Memory = V4l2.MemoryMmap,
            };

            if (!TryIoctl(V4l2.QueryBuffer, ref buffer, "query a frame buffer"))
                return false;

            var address = NativeMethods.mmap(
                0,
                buffer.Length,
                Webcam.ProtectionRead | Webcam.ProtectionWrite,
                Webcam.MapShared,
                _fd,
                buffer.Offset
            );

            if (address == Webcam.MapFailed)
            {
                Debug.WriteLine(
                    $"Failed to map a frame buffer of '{_devicePath}': error {Marshal.GetLastPInvokeError()}."
                );
                return false;
            }

            _buffers.Add((address, buffer.Length));
        }

        // The camera's exposure controls, remembered to be put back
        if (
            Webcam.TryGetControl(_devicePath, _fd, V4l2.ControlExposureAuto) is not { } exposureMode
        )
        {
            Debug.WriteLine($"'{_devicePath}' has no manual exposure, so it cannot meter light.");
            return false;
        }

        if (
            Webcam.TryQueryControl(_devicePath, _fd, V4l2.ControlExposureAbsolute)
            is not { } exposureRange
        )
            return false;

        _exposureRange = exposureRange;
        _previousExposureMode = exposureMode;
        _previousExposure = Webcam.TryGetControl(_devicePath, _fd, V4l2.ControlExposureAbsolute);

        var gainRange = Webcam.TryQueryControl(_devicePath, _fd, V4l2.ControlGain);
        _previousGain = gainRange is not null
            ? Webcam.TryGetControl(_devicePath, _fd, V4l2.ControlGain)
            : null;

        // From here on, the settings are put back however far this gets
        _areControlsChanged = true;

        if (!Webcam.TrySetControl(_devicePath, _fd, V4l2.ControlExposureAuto, V4l2.ExposureManual))
            return false;

        // Automatic exposure turns the gain up in the dark as well, and leaves it wherever it was
        // last; the camera's default is the one setting that reads the same every time
        if (gainRange is { } range)
            Webcam.TrySetControl(_devicePath, _fd, V4l2.ControlGain, range.DefaultValue);

        _exposure = exposureSetting is { } hint
            ? Webcam.Align(hint, exposureRange)
            : Webcam.Align(exposureRange.DefaultValue, exposureRange);

        if (!Webcam.TrySetControl(_devicePath, _fd, V4l2.ControlExposureAbsolute, _exposure))
            return false;

        for (var index = 0u; index < _buffers.Count; index++)
        {
            var buffer = new V4l2Buffer
            {
                Index = index,
                Type = V4l2.BufferTypeVideoCapture,
                Memory = V4l2.MemoryMmap,
            };

            if (!TryIoctl(V4l2.QueueBuffer, ref buffer, "queue a frame buffer"))
                return false;
        }

        var streamType = (int)V4l2.BufferTypeVideoCapture;
        if (!TryIoctl(V4l2.StreamOn, ref streamType, "start the stream"))
            return false;

        _isStreaming = true;
        _settlingFrames = Webcam.InitialSettlingFrameCount;
        _lastFrameTimestamp = Stopwatch.GetTimestamp();

        return true;
    }

    // Reads the next frame whose exposure has settled, moving the exposure toward the band when
    // the frame falls outside it. The frame is read as it was taken, at the exposure before any move.
    public WebcamMeterResult ReadNext(TimeSpan timeout, out WebcamLightReading reading)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        reading = default;

        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
                return WebcamMeterResult.Timeout;

            var pollFd = new PollFd { Fd = _fd, Events = Webcam.PollIn };
            var polled = NativeMethods.poll(
                ref pollFd,
                1,
                (int)Math.Ceiling(remaining.TotalMilliseconds)
            );

            if (polled < 0)
            {
                if (Marshal.GetLastPInvokeError() == Webcam.Interrupted)
                    continue;

                Debug.WriteLine(
                    $"Failed to wait for a frame from '{_devicePath}': error {Marshal.GetLastPInvokeError()}."
                );
                return WebcamMeterResult.Failed;
            }

            if (polled == 0)
                return WebcamMeterResult.Timeout;

            if ((pollFd.ReturnedEvents & (Webcam.PollError | Webcam.PollHangUp)) != 0)
            {
                Debug.WriteLine($"'{_devicePath}' stopped streaming.");
                return WebcamMeterResult.Failed;
            }

            if ((pollFd.ReturnedEvents & Webcam.PollIn) == 0)
                continue;

            if (!TryTakeFrame(out var luma))
                return WebcamMeterResult.Failed;

            // A frame the driver marked damaged, or delivered short, is passed over
            if (luma is not { } frameLuma)
                continue;

            Debug.WriteLine(
                $"Frame from '{_devicePath}' at exposure {_exposure}: luma {frameLuma:F3}, "
                    + $"{Stopwatch.GetElapsedTime(_lastFrameTimestamp).TotalMilliseconds:F0} ms after the last."
            );
            _lastFrameTimestamp = Stopwatch.GetTimestamp();

            // The first frames of a stream, and the first after the exposure changes, were taken
            // at the old exposure or at none
            if (_settlingFrames > 0)
            {
                _settlingFrames--;
                continue;
            }

            var exposure = _exposure;
            var nextExposure = Webcam.GetNextExposure(frameLuma, exposure, _exposureRange);

            IsExposureSettled = nextExposure == exposure;
            if (!IsExposureSettled)
            {
                if (
                    !Webcam.TrySetControl(
                        _devicePath,
                        _fd,
                        V4l2.ControlExposureAbsolute,
                        nextExposure
                    )
                )
                    return WebcamMeterResult.Failed;

                _exposure = nextExposure;
                _settlingFrames = Webcam.SettlingFrameCount;
            }

            reading = new WebcamLightReading(
                frameLuma,
                TimeSpan.FromTicks(exposure * Webcam.ExposureUnitTicks),
                exposure
            );

            return WebcamMeterResult.Reading;
        }
    }

    // Reads until the exposure settles, moving it at most the given number of times, and gives
    // the last frame
    public WebcamLightReading? TryMeterLight(int maximumAdjustments)
    {
        var adjustments = 0;
        while (true)
        {
            if (ReadNext(Webcam.FrameTimeout, out var reading) != WebcamMeterResult.Reading)
                return null;

            if (IsExposureSettled || adjustments >= maximumAdjustments)
                return reading;

            adjustments++;
        }
    }

    // Takes the frame waiting in the queue, reads its brightness and gives its buffer back
    private bool TryTakeFrame(out double? luma)
    {
        luma = null;

        var buffer = new V4l2Buffer
        {
            Type = V4l2.BufferTypeVideoCapture,
            Memory = V4l2.MemoryMmap,
        };

        if (!TryIoctl(V4l2.DequeueBuffer, ref buffer, "take a frame"))
            return false;

        if (
            (buffer.Flags & V4l2.BufferFlagError) == 0
            && buffer.BytesUsed >= _layout.FrameSize
            && buffer.Index < _buffers.Count
        )
        {
            Marshal.Copy(_buffers[(int)buffer.Index].Address, _frame, 0, _layout.FrameSize);
            luma = _layout.GetLuma(_frame);
        }
        else
        {
            Debug.WriteLine(
                $"'{_devicePath}' delivered a damaged frame: flags {buffer.Flags:X}, {buffer.BytesUsed} of {_layout.FrameSize} bytes."
            );
        }

        var requeue = new V4l2Buffer
        {
            Index = buffer.Index,
            Type = V4l2.BufferTypeVideoCapture,
            Memory = V4l2.MemoryMmap,
        };

        return TryIoctl(V4l2.QueueBuffer, ref requeue, "queue a frame buffer");
    }

    private bool TryIoctl(nuint request, ref V4l2Format argument, string action) =>
        Webcam.TryIoctl(NativeMethods.ioctl, _devicePath, _fd, request, ref argument, action);

    private bool TryIoctl(nuint request, ref V4l2StreamParm argument, string action) =>
        Webcam.TryIoctl(NativeMethods.ioctl, _devicePath, _fd, request, ref argument, action);

    private bool TryIoctl(nuint request, ref V4l2RequestBuffers argument, string action) =>
        Webcam.TryIoctl(NativeMethods.ioctl, _devicePath, _fd, request, ref argument, action);

    private bool TryIoctl(nuint request, ref V4l2Buffer argument, string action) =>
        Webcam.TryIoctl(NativeMethods.ioctl, _devicePath, _fd, request, ref argument, action);

    private bool TryIoctl(nuint request, ref int argument, string action) =>
        Webcam.TryIoctl(NativeMethods.ioctl, _devicePath, _fd, request, ref argument, action);

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        // The stream stops first, then the settings go back while the camera is still open and
        // still this program's: a camera asked for a format by another program within a fraction of
        // a second of a stream stopping can stop answering altogether, so the camera is kept, which
        // tells any such program it is busy, until a moment has passed, and only then let go of
        var phaseTimestamp = Stopwatch.GetTimestamp();

        if (_isStreaming)
        {
            var streamType = (int)V4l2.BufferTypeVideoCapture;
            TryIoctl(V4l2.StreamOff, ref streamType, "stop the stream");

            Debug.WriteLine(
                $"Stopped the stream from '{_devicePath}' in {Stopwatch.GetElapsedTime(phaseTimestamp).TotalMilliseconds:F0} ms."
            );
        }

        // The exposure goes back first, while the camera still takes one: in automatic mode the
        // control is read-only
        if (_areControlsChanged)
        {
            if (_previousExposure is { } previousExposure)
                Webcam.TrySetControl(
                    _devicePath,
                    _fd,
                    V4l2.ControlExposureAbsolute,
                    previousExposure
                );

            if (_previousGain is { } previousGain)
                Webcam.TrySetControl(_devicePath, _fd, V4l2.ControlGain, previousGain);

            Webcam.TrySetControl(_devicePath, _fd, V4l2.ControlExposureAuto, _previousExposureMode);
        }

        if (_isStreaming)
            Thread.Sleep(Webcam.QuietAfterStreaming);

        foreach (var (address, length) in _buffers)
            NativeMethods.munmap(address, length);

        _buffers.Clear();

        if (_areBuffersRequested)
        {
            var release = new V4l2RequestBuffers
            {
                Count = 0,
                Type = V4l2.BufferTypeVideoCapture,
                Memory = V4l2.MemoryMmap,
            };

            TryIoctl(V4l2.RequestBuffers, ref release, "release the frame buffers");
        }

        NativeMethods.close(_fd);

        Debug.WriteLine(
            $"Let go of '{_devicePath}' after {Stopwatch.GetElapsedTime(_openTimestamp).TotalMilliseconds:F0} ms."
        );
    }
}
