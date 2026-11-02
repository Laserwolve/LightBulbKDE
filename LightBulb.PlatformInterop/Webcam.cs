using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LightBulb.PlatformInterop.Internal;

namespace LightBulb.PlatformInterop;

// What a camera saw of the room: the average brightness of a frame, from 0 at video black to 1 at
// video white, the exposure it was taken with, and that exposure as the camera's control counts it
public readonly record struct WebcamLightReading(
    double Luma,
    TimeSpan Exposure,
    int ExposureSetting
);

// A camera the kernel offers through Video4Linux, read as a light meter. A camera left to itself
// exposes every room to the same middle grey, so a meter takes the camera into manual exposure,
// finds the exposure that brings a small frame to a known brightness, as the camera's own metering
// would, and puts the camera's settings back when it is done. The exposure the room needs is then
// the measure of its light, which the camera keeps exactly, and the frame's brightness corrects
// only for the little distance left to the target.
[SupportedOSPlatform("linux")]
public partial class Webcam(string devicePath, string name)
{
    public string DevicePath { get; } = devicePath;

    public string Name { get; } = name;

    public override string ToString() => Name;

    // Whether another program has the camera open, read from the file handles every process of
    // this user publishes, so that a camera somebody is recording with is left alone
    public bool IsInUse()
    {
        foreach (var processDirPath in Directory.EnumerateDirectories("/proc"))
        {
            if (
                !int.TryParse(Path.GetFileName(processDirPath), out var processId)
                || processId == Environment.ProcessId
            )
                continue;

            try
            {
                foreach (
                    var handlePath in Directory.EnumerateFileSystemEntries(
                        Path.Combine(processDirPath, "fd")
                    )
                )
                {
                    if (new FileInfo(handlePath).LinkTarget == DevicePath)
                        return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another user's process, or one that exited while it was being read
            }
        }

        return false;
    }

    // Reads the light once, starting from the given exposure setting, or the camera's default
    // without one, and moving it at most the given number of times before the camera is let go of
    public WebcamLightReading? TryMeterLight(
        int? exposureSetting = null,
        int maximumAdjustments = DefaultMaximumAdjustments
    )
    {
        using var meter = TryOpenMeter(exposureSetting);
        return meter?.TryMeterLight(maximumAdjustments);
    }

    // Opens the camera to be read frame by frame, at the given frame interval if the camera can
    // keep it. Nothing is returned when the camera is streaming to another program, has no manual
    // exposure, or fails on the way.
    public WebcamMeter? TryOpenMeter(int? exposureSetting = null, TimeSpan? frameInterval = null)
    {
        var fd = NativeMethods.open(DevicePath, OpenReadWrite | OpenNonBlocking | OpenCloseOnExec);
        if (fd < 0)
        {
            Debug.WriteLine(
                $"Failed to open '{DevicePath}': error {Marshal.GetLastPInvokeError()}."
            );
            return null;
        }

        var meter = new WebcamMeter(DevicePath, fd);
        if (meter.TrySetUp(exposureSetting, frameInterval))
            return meter;

        meter.Dispose();
        return null;
    }
}

public partial class Webcam
{
    internal delegate int IoctlCall<T>(int fd, nuint request, ref T argument)
        where T : struct;

    internal readonly record struct ControlRange(
        int Minimum,
        int Maximum,
        int Step,
        int DefaultValue
    );

    // Where the brightness of each pixel sits in a frame, for the formats that store it as a byte,
    // and how the room's light is read from it
    internal readonly record struct LumaLayout(
        int FrameSize,
        int Offset,
        int Stride,
        int Width,
        int Height
    )
    {
        public static LumaLayout? TryGet(V4l2PixelFormat pixel)
        {
            var width = (int)pixel.Width;
            var height = (int)pixel.Height;
            var frameSize = (int)pixel.SizeImage;

            LumaLayout? layout = pixel.PixelFormat switch
            {
                // Packed 4:2:2, with the brightness on even or on odd bytes
                V4l2.PixelFormatYuyv or V4l2.PixelFormatYvyu => new(frameSize, 0, 2, width, height),
                V4l2.PixelFormatUyvy or V4l2.PixelFormatVyuy => new(frameSize, 1, 2, width, height),
                // Greyscale and the planar formats start with one byte of brightness per pixel
                V4l2.PixelFormatGrey
                or V4l2.PixelFormatNv12
                or V4l2.PixelFormatNv21
                or V4l2.PixelFormatYuv420
                or V4l2.PixelFormatYvu420 => new(frameSize, 0, 1, width, height),
                _ => null,
            };

            return
                layout is { } l
                && l.Width > 0
                && l.Height > 0
                && l.Offset + (l.Width * l.Height - 1) * l.Stride < l.FrameSize
                ? layout
                : null;
        }

        // The brightness of the room in the frame, from 0 at video black to 1 at video white. Only
        // the top of the frame is read, which looks over the person at the room behind them, and
        // the darkest and brightest tenths of its pixels are left out, which is where a dark shirt
        // or a window shows up.
        public double GetLuma(ReadOnlySpan<byte> frame)
        {
            var rows = Math.Max(1, (int)(Height * MeteredShare));
            var count = rows * Width;

            Span<int> histogram = stackalloc int[256];
            for (var i = 0; i < count; i++)
                histogram[frame[Offset + i * Stride]]++;

            var dropped = count / 10;
            var lowRank = dropped;
            var highRank = count - dropped;

            var sum = 0L;
            var seen = 0;
            for (var value = 0; value < histogram.Length; value++)
            {
                var pixels = histogram[value];
                if (pixels == 0)
                    continue;

                var from = Math.Max(seen, lowRank);
                var to = Math.Min(seen + pixels, highRank);
                if (to > from)
                    sum += (long)value * (to - from);

                seen += pixels;
            }

            var average = (double)sum / (highRank - lowRank);
            return Math.Clamp((average - VideoBlack) / (VideoWhite - VideoBlack), 0, 1);
        }
    }

    private const int DefaultMaximumAdjustments = 4;

    internal const uint FrameWidth = 160;
    internal const uint FrameHeight = 120;
    internal const uint BufferCount = 2;

    // Frames passed over at the start of the stream and after each change of exposure
    internal const int InitialSettlingFrameCount = 1;
    internal const int SettlingFrameCount = 2;

    // A frame is accepted between these, and otherwise the exposure moves to bring the next ones
    // to the target. Nothing above the ceiling can be told apart, so such a frame moves it a long way.
    private const double LowLuma = 0.25;
    private const double HighLuma = 0.55;
    private const double TargetLuma = 0.35;
    private const double ClippedLuma = 0.95;
    private const double ClippedExposureFactor = 1.0 / 8;
    private const double MinimumLuma = 1.0 / 255;

    // How a frame's brightness answers to the light, as a power, closely enough to aim the
    // exposure with: cameras keep the dark end of their range nearly linear and compress the
    // bright end, and undershooting the target is harmless where overshooting it can send the
    // next frame out of the band. The reading's own conversion need not agree, since whatever is
    // left over is only the distance from the frame to the target.
    private const double DarkResponseGamma = 1.0;
    private const double BrightResponseGamma = 1.6;

    // The share of the frame read, from the top
    private const double MeteredShare = 0.4;

    private const int VideoBlack = 16;
    private const int VideoWhite = 235;

    // The exposure control counts hundreds of microseconds
    internal const long ExposureUnitTicks = 1000;

    // How long the camera is kept after its stream stops, before it is let go of
    internal static readonly TimeSpan QuietAfterStreaming = TimeSpan.FromMilliseconds(500);

    // The longest exposure used, a tenth of a second: the camera slows to the exposure's frame rate,
    // which would make a reading in a dark room take seconds at the longest it allows
    internal const int MaximumExposure = 1000;

    // How long a frame may take to come before the camera counts as stuck
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);

    // open(2), mmap(2) and poll(2)
    private const int OpenReadOnly = 0x0;
    private const int OpenReadWrite = 0x2;
    private const int OpenNonBlocking = 0x800;
    private const int OpenCloseOnExec = 0x80000;
    internal const int ProtectionRead = 0x1;
    internal const int ProtectionWrite = 0x2;
    internal const int MapShared = 0x1;
    internal static readonly nint MapFailed = -1;
    internal const short PollIn = 0x1;
    internal const short PollError = 0x8;
    internal const short PollHangUp = 0x10;
    internal const int Interrupted = 4;

    // Every camera the kernel offers that can stream video, in the order of their device nodes
    public static IReadOnlyList<Webcam> GetAll()
    {
        var webcams = new List<Webcam>();
        if (!Directory.Exists("/dev"))
            return webcams;

        var devicePaths = Directory
            .EnumerateFiles("/dev", "video*")
            .OrderBy(path =>
                int.TryParse(Path.GetFileName(path)["video".Length..], out var number)
                    ? number
                    : int.MaxValue
            )
            .ThenBy(path => path, StringComparer.Ordinal);

        foreach (var devicePath in devicePaths)
        {
            var fd = NativeMethods.open(
                devicePath,
                OpenReadOnly | OpenNonBlocking | OpenCloseOnExec
            );

            if (fd < 0)
                continue;

            try
            {
                var capability = new V4l2Capability();
                if (NativeMethods.ioctl(fd, V4l2.QueryCapability, ref capability) < 0)
                    continue;

                // A device may have fewer capabilities than its driver as a whole
                var capabilities =
                    (capability.Capabilities & V4l2.CapabilityDeviceCapabilities) != 0
                        ? capability.DeviceCapabilities
                        : capability.Capabilities;

                if (
                    (capabilities & V4l2.CapabilityVideoCapture) == 0
                    || (capabilities & V4l2.CapabilityStreaming) == 0
                )
                    continue;

                webcams.Add(new Webcam(devicePath, ReadString(capability.Card)));
            }
            finally
            {
                NativeMethods.close(fd);
            }
        }

        return webcams;
    }

    // The exposure for the next frames: the same while the frame's brightness is within the
    // band, and otherwise the one expected to bring it to the target, as far as the camera goes
    internal static int GetNextExposure(double luma, int exposure, ControlRange range)
    {
        if (luma is >= LowLuma and <= HighLuma)
            return exposure;

        var factor =
            luma >= ClippedLuma
                ? ClippedExposureFactor
                : Math.Pow(
                    TargetLuma / Math.Max(luma, MinimumLuma),
                    luma < TargetLuma ? DarkResponseGamma : BrightResponseGamma
                );

        return Align(
            (int)Math.Round(Math.Clamp(exposure * factor, range.Minimum, range.Maximum)),
            range
        );
    }

    // The nearest value the control accepts, which moves in steps from its minimum
    internal static int Align(int value, ControlRange range)
    {
        var clamped = Math.Clamp(value, range.Minimum, Math.Min(range.Maximum, MaximumExposure));
        return range.Minimum + (clamped - range.Minimum) / range.Step * range.Step;
    }

    internal static ControlRange? TryQueryControl(string devicePath, int fd, uint id)
    {
        var query = new V4l2QueryControl { Id = id };
        if (
            !TryIoctl(
                NativeMethods.ioctl,
                devicePath,
                fd,
                V4l2.QueryControl,
                ref query,
                $"query control {id:X}"
            )
            || (query.Flags & V4l2.ControlFlagDisabled) != 0
        )
            return null;

        return new ControlRange(
            query.Minimum,
            query.Maximum,
            Math.Max(query.Step, 1),
            query.DefaultValue
        );
    }

    internal static int? TryGetControl(string devicePath, int fd, uint id)
    {
        var control = new V4l2Control { Id = id };
        return TryIoctl(
            NativeMethods.ioctl,
            devicePath,
            fd,
            V4l2.GetControl,
            ref control,
            $"read control {id:X}"
        )
            ? control.Value
            : null;
    }

    internal static bool TrySetControl(string devicePath, int fd, uint id, int value)
    {
        var control = new V4l2Control { Id = id, Value = value };
        return TryIoctl(
            NativeMethods.ioctl,
            devicePath,
            fd,
            V4l2.SetControl,
            ref control,
            $"set control {id:X} to {value}"
        );
    }

    internal static bool TryIoctl<T>(
        IoctlCall<T> call,
        string devicePath,
        int fd,
        nuint request,
        ref T argument,
        string action
    )
        where T : struct
    {
        while (true)
        {
            if (call(fd, request, ref argument) >= 0)
                return true;

            var error = Marshal.GetLastPInvokeError();
            if (error == Interrupted)
                continue;

            Debug.WriteLine($"Failed to {action} on '{devicePath}': error {error}.");
            return false;
        }
    }

    private static string ReadString(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(length >= 0 ? bytes[..length] : bytes).Trim();
    }
}
