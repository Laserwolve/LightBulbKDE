using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LightBulb.PlatformInterop.Internal;

// The parts of <linux/videodev2.h> that a light meter needs, laid out as they are on 64-bit Linux.
// Each request number encodes the size of its struct, so a wrong layout fails the call rather than
// corrupting memory.
internal static class V4l2
{
    // Requests
    public const nuint QueryCapability = 0x80685600;
    public const nuint SetFormat = 0xC0D05605;
    public const nuint RequestBuffers = 0xC0145608;
    public const nuint QueryBuffer = 0xC0585609;
    public const nuint QueueBuffer = 0xC058560F;
    public const nuint DequeueBuffer = 0xC0585611;
    public const nuint StreamOn = 0x40045612;
    public const nuint StreamOff = 0x40045613;
    public const nuint GetControl = 0xC008561B;
    public const nuint SetControl = 0xC008561C;
    public const nuint QueryControl = 0xC0445624;
    public const nuint SetStreamParm = 0xC0CC5616;

    // Capabilities
    public const uint CapabilityVideoCapture = 0x1;
    public const uint CapabilityStreaming = 0x04000000;
    public const uint CapabilityDeviceCapabilities = 0x80000000;

    // Buffers
    public const uint BufferTypeVideoCapture = 1;
    public const uint MemoryMmap = 1;
    public const uint FieldNone = 1;
    public const uint BufferFlagError = 0x40;

    // Controls
    public const uint ControlGain = 0x00980913;
    public const uint ControlExposureAuto = 0x009A0901;
    public const uint ControlExposureAbsolute = 0x009A0902;
    public const int ExposureManual = 1;
    public const uint ControlFlagDisabled = 0x1;

    // Pixel formats, as the four-character codes the kernel uses
    public const uint PixelFormatYuyv = 0x56595559;
    public const uint PixelFormatYvyu = 0x55595659;
    public const uint PixelFormatUyvy = 0x59565955;
    public const uint PixelFormatVyuy = 0x59555956;
    public const uint PixelFormatGrey = 0x59455247;
    public const uint PixelFormatNv12 = 0x3231564E;
    public const uint PixelFormatNv21 = 0x3132564E;
    public const uint PixelFormatYuv420 = 0x32315559;
    public const uint PixelFormatYvu420 = 0x32315659;
}

[InlineArray(16)]
internal struct Bytes16
{
    private byte _element;
}

[InlineArray(32)]
internal struct Bytes32
{
    private byte _element;
}

[StructLayout(LayoutKind.Sequential, Size = 104)]
internal struct V4l2Capability
{
    public Bytes16 Driver;
    public Bytes32 Card;
    public Bytes32 BusInfo;
    public uint Version;
    public uint Capabilities;
    public uint DeviceCapabilities;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
}

[StructLayout(LayoutKind.Sequential, Size = 48)]
internal struct V4l2PixelFormat
{
    public uint Width;
    public uint Height;
    public uint PixelFormat;
    public uint Field;
    public uint BytesPerLine;
    public uint SizeImage;
    public uint ColorSpace;
    public uint Private;
    public uint Flags;
    public uint Encoding;
    public uint Quantization;
    public uint TransferFunction;
}

// The format is a union of several layouts after its type, aligned to 8 bytes
[StructLayout(LayoutKind.Explicit, Size = 208)]
internal struct V4l2Format
{
    [FieldOffset(0)]
    public uint Type;

    [FieldOffset(8)]
    public V4l2PixelFormat Pixel;
}

[StructLayout(LayoutKind.Sequential, Size = 20)]
internal struct V4l2RequestBuffers
{
    public uint Count;
    public uint Type;
    public uint Memory;
    public uint Capabilities;
    public byte Flags;
    public byte Reserved0;
    public byte Reserved1;
    public byte Reserved2;
}

// Only the fields a capture needs are named; the timestamp, timecode and the rest stay zero
[StructLayout(LayoutKind.Explicit, Size = 88)]
internal struct V4l2Buffer
{
    [FieldOffset(0)]
    public uint Index;

    [FieldOffset(4)]
    public uint Type;

    [FieldOffset(8)]
    public uint BytesUsed;

    [FieldOffset(12)]
    public uint Flags;

    [FieldOffset(16)]
    public uint Field;

    [FieldOffset(56)]
    public uint Sequence;

    [FieldOffset(60)]
    public uint Memory;

    [FieldOffset(64)]
    public uint Offset;

    [FieldOffset(72)]
    public uint Length;
}

[StructLayout(LayoutKind.Sequential, Size = 8)]
internal struct V4l2Control
{
    public uint Id;
    public int Value;
}

[StructLayout(LayoutKind.Sequential, Size = 68)]
internal struct V4l2QueryControl
{
    public uint Id;
    public uint Type;
    public Bytes32 Name;
    public int Minimum;
    public int Maximum;
    public int Step;
    public int DefaultValue;
    public uint Flags;
    public uint Reserved0;
    public uint Reserved1;
}

[StructLayout(LayoutKind.Sequential, Size = 8)]
internal struct PollFd
{
    public int Fd;
    public short Events;
    public short ReturnedEvents;
}

// The streaming parameters, of which only the capture side's frame interval is used
[StructLayout(LayoutKind.Explicit, Size = 204)]
internal struct V4l2StreamParm
{
    [FieldOffset(0)]
    public uint Type;

    [FieldOffset(4)]
    public uint Capability;

    [FieldOffset(8)]
    public uint CaptureMode;

    [FieldOffset(12)]
    public uint TimePerFrameNumerator;

    [FieldOffset(16)]
    public uint TimePerFrameDenominator;
}

// The parts of <sys/inotify.h> that watching a device node needs
internal static class Inotify
{
    public const int NonBlocking = 0x800;
    public const int CloseOnExec = 0x80000;

    public const uint Open = 0x20;
    public const uint CloseWrite = 0x8;
    public const uint CloseNoWrite = 0x10;

    // An event is a watch descriptor, a mask, a cookie and a name length, each 32 bits, then the name
    public const int EventHeaderSize = 16;
    public const int MaskOffset = 4;
    public const int NameLengthOffset = 12;
}
