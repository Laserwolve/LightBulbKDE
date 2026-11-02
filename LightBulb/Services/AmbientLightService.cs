using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LightBulb.Core;
using LightBulb.PlatformInterop;

namespace LightBulb.Services;

public enum AmbientLightStatus
{
    // Nothing is asking for readings
    Idle,

    // The latest reading succeeded
    Read,
    NoCamera,
    CameraInUse,

    // The camera has been lent to another program, which is yet to be done with it
    CameraLent,

    // The camera has only just appeared, and is being left alone while the system sets it up
    CameraSettling,

    // The room is being taken as the darkest it gets, with the screen brightened for a moment
    Calibrating,
    Failed,
}

// Reads the light in the room through the webcam, as a phone reads its light sensor, on a
// background worker that runs only while something asks for readings: the dashboard while the
// screen follows the room, and the settings while they are being looked at. The camera is either
// read every so often or, when the settings say to keep it on, streamed from all the time. A
// stream is this program's alone, so other programs find the camera busy until it is lent to them,
// which stops the readings until they have had the camera and let it go.
//
// What the screen follows is not the raw reading. The screen's own glow, as far as it is known,
// is taken out first; then the readings are filtered the way a phone's are, rising quickly and
// falling slowly, so that a light switched on is answered at once and a shadow passing is not.
//
// Hand-overs are kept apart on purpose. A camera asked for a format within a fraction of a second
// of a stream stopping can stop answering altogether, and then needs unplugging, so the camera is
// never let go of while another program is asking for it, and a camera another program has just
// closed is given a moment before it is opened here.
public partial class AmbientLightService : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread? _worker;

    // Set by the app, read by the worker
    private bool _isMeteringRequested;
    private bool _isPreviewRequested;
    private bool _areCamerasRequested;
    private bool _isCameraLent;
    private bool _isDarkCalibrationRequested;
    private double _screenBrightness = 1;

    // Owned by the worker
    private int? _exposureSetting;
    private int _failures;
    private double? _latestStops;
    private double? _filteredStops;
    private double? _followedStops;
    private DateTimeOffset _lastReadingTimestamp = DateTimeOffset.MinValue;
    private WebcamWatch? _watch;
    private string? _watchedDevicePath;
    private bool _hasSettledAtStart;

    public AmbientLightService(SettingsService settingsService)
    {
        _settingsService = settingsService;

        // The worker waits on the camera itself, so it has a thread of its own rather than one
        // borrowed from the pool
        if (OperatingSystem.IsLinux())
        {
            _worker = new Thread(Run) { IsBackground = true, Name = "Ambient light" };
            _worker.Start();
        }
    }

    // Cameras are read through Video4Linux
    public static bool IsSupported { get; } = OperatingSystem.IsLinux();

    // The latest reading, in the camera's own stops, with the screen's glow taken out
    [ObservableProperty]
    public partial double? LatestStops { get; private set; }

    // The reading the screen follows: the filtered one, moved only when it has drifted a little
    // way from where the screen last followed it, so that the dimming does not flicker
    [ObservableProperty]
    public partial double? Stops { get; private set; }

    [ObservableProperty]
    public partial AmbientLightStatus Status { get; private set; }

    // The cameras found when they were last looked for, by name
    [ObservableProperty]
    public partial IReadOnlyList<string> Cameras { get; private set; } = [];

    // Whether the camera has been lent to another program
    [ObservableProperty]
    public partial bool IsCameraLent { get; private set; }

    // A brightness the screen is asked to hold for a moment, while the room is read with the
    // screen brightened to measure its glow
    [ObservableProperty]
    public partial double? ForcedBrightness { get; private set; }

    // Whether the screen is following the room, which asks for readings
    public bool IsMeteringRequested
    {
        get
        {
            using (_lock.EnterScope())
                return _isMeteringRequested;
        }
        set => Request(ref _isMeteringRequested, value);
    }

    // Whether the readings are being watched, which asks for them every few seconds
    public bool IsPreviewRequested
    {
        get
        {
            using (_lock.EnterScope())
                return _isPreviewRequested;
        }
        set => Request(ref _isPreviewRequested, value);
    }

    // The brightness the screen is at, which the dashboard reports as it changes it, so that the
    // screen's own glow can be taken out of the readings
    public double ScreenBrightness
    {
        get
        {
            using (_lock.EnterScope())
                return _screenBrightness;
        }
        set
        {
            using (_lock.EnterScope())
                _screenBrightness = value;
        }
    }

    // Asks for the cameras to be looked for again, which happens as soon as none is being
    // streamed from, since looking opens each one for a moment
    public void RequestCameras() => Request(ref _areCamerasRequested, true);

    // Lets another program have the camera: the readings stop until that program has had the
    // camera and let it go, or until nobody has taken it for a while
    public void LendCamera() => SetCameraLent(true);

    public void TakeCameraBack() => SetCameraLent(false);

    // Takes the room as it is for the darkest it gets, and measures how much the screen itself
    // lights it: the room is read with the screen as it is and again with the screen brightened for
    // a moment, and the difference between the two is the screen's doing, to be taken out of every
    // reading from then on
    public void CalibrateDarkReference() => Request(ref _isDarkCalibrationRequested, true);

    private void SetCameraLent(bool isLent)
    {
        if (!Request(ref _isCameraLent, isLent))
            return;

        Dispatcher.UIThread.Post(() => IsCameraLent = isLent);
    }

    private bool Request(ref bool request, bool value)
    {
        using (_lock.EnterScope())
        {
            if (request == value)
                return false;

            request = value;
        }

        Wake();
        return true;
    }

    private void Wake()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already due to wake up
        }
        catch (ObjectDisposedException)
        {
            // Shutting down
        }
    }

    // Waits for the time given, or until something is asked for
    private void Wait(TimeSpan timeout) => _wakeSignal.Wait(timeout, _cancellation.Token);

    // Waits for the time given, whatever is asked for meanwhile
    private void Pause(TimeSpan duration) => _cancellation.Token.WaitHandle.WaitOne(duration);

    [SupportedOSPlatform("linux")]
    private void Run()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                bool isMeteringRequested;
                bool isPreviewRequested;
                bool areCamerasRequested;
                bool isCameraLent;
                bool isDarkCalibrationRequested;
                using (_lock.EnterScope())
                {
                    isMeteringRequested = _isMeteringRequested;
                    isPreviewRequested = _isPreviewRequested;
                    areCamerasRequested = _areCamerasRequested;
                    isCameraLent = _isCameraLent;
                    isDarkCalibrationRequested = _isDarkCalibrationRequested;
                    _areCamerasRequested = false;
                }

                if (!isMeteringRequested && !isPreviewRequested)
                {
                    if (areCamerasRequested)
                        LookForWebcam();

                    // A reading from a camera that has gone is let go of in time
                    Publish(AmbientLightStatus.Idle);
                    Wait(StaleAfter);
                    continue;
                }

                if (isCameraLent)
                {
                    Lend();
                    continue;
                }

                if (isDarkCalibrationRequested)
                {
                    CalibrateDark();
                    continue;
                }

                if (_settingsService.IsAmbientLightContinuous)
                {
                    Stream();
                    continue;
                }

                // Readings come at the interval asked for, counted from the start of one to the
                // start of the next, with a small gap kept between them whatever the interval
                var startTimestamp = Stopwatch.GetTimestamp();
                Measure();

                var interval = isPreviewRequested
                    ? PreviewInterval
                    : _settingsService.AmbientLightInterval;

                if (interval < MinimumInterval)
                    interval = MinimumInterval;

                var remaining = interval - Stopwatch.GetElapsedTime(startTimestamp);
                Wait(remaining > MinimumGap ? remaining : MinimumGap);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read the ambient light: {ex}");
                Publish(AmbientLightStatus.Failed);

                try
                {
                    Wait(FailureDelay);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _watch?.Dispose();
    }

    // Reads the camera once and follows what it saw
    [SupportedOSPlatform("linux")]
    private void Measure()
    {
        if (TryRead(out var stops, out var screenBrightness))
            Publish(AmbientLightStatus.Read, stops, screenBrightness);
    }

    // Reads the camera once, in a short stream, and says what the screen was at meanwhile. A
    // reading that does not come has its reason published here.
    [SupportedOSPlatform("linux")]
    private bool TryRead(out double stops, out double screenBrightness)
    {
        stops = 0;
        screenBrightness = ScreenBrightness;

        SettleAfterOthers();
        WaitAfterLongStream();

        if (LookForWebcam() is not { } webcam)
        {
            Publish(AmbientLightStatus.NoCamera);
            return false;
        }

        if (HasJustAppeared(webcam))
        {
            Publish(AmbientLightStatus.CameraSettling);
            return false;
        }

        // A camera somebody is recording with is left alone
        if (webcam.IsInUse())
        {
            Publish(AmbientLightStatus.CameraInUse);
            return false;
        }

        screenBrightness = ScreenBrightness;
        var reading = webcam.TryMeterLight(_exposureSetting);
        _watch?.Drain();

        if (reading is not { } taken)
        {
            Publish(AmbientLightStatus.Failed);
            _failures++;
            return false;
        }

        // The next reading starts from this one's exposure, which is usually right already
        _exposureSetting = taken.ExposureSetting;
        _failures = 0;

        stops = AmbientLight.GetStops(taken.Luma, taken.Exposure);
        return true;
    }

    // Reads the room with the screen as it is and again with the screen brightened, takes the
    // difference for the screen's glow, and the room without it for the darkest it gets
    [SupportedOSPlatform("linux")]
    private void CalibrateDark()
    {
        using (_lock.EnterScope())
            _isDarkCalibrationRequested = false;

        Publish(AmbientLightStatus.Calibrating);

        if (!TryRead(out var dimStops, out var dimBrightness))
            return;

        Dispatcher.UIThread.Post(() => ForcedBrightness = CalibrationBrightness);

        try
        {
            // The screen fades to the brightness asked for, and the room answers at once
            Pause(CalibrationDelay);

            if (!TryRead(out var brightStops, out var brightBrightness))
                return;

            var glow = AmbientLight.GetGlowPerDimming(
                dimStops,
                dimBrightness,
                brightStops,
                brightBrightness
            );

            var room = AmbientLight.CompensateForScreen(brightStops, brightBrightness, glow);

            Dispatcher.UIThread.Post(() =>
            {
                _settingsService.AmbientLightGlow = glow;
                _settingsService.AmbientLightDarkStops = room;
            });

            // The screen starts over from the room as it is
            _filteredStops = null;
            _followedStops = null;
            Publish(AmbientLightStatus.Read, brightStops, brightBrightness, glow);
        }
        finally
        {
            Dispatcher.UIThread.Post(() => ForcedBrightness = null);
        }
    }

    // Streams from the camera, following a reading every second, until nothing asks for one any
    // more, the settings say to stop keeping the camera on, the camera is wanted for something
    // else, or it fails
    [SupportedOSPlatform("linux")]
    private void Stream()
    {
        SettleAfterOthers();
        WaitAfterLongStream();

        if (LookForWebcam() is not { } webcam)
        {
            Publish(AmbientLightStatus.NoCamera);
            Wait(RetryInterval);
            return;
        }

        if (HasJustAppeared(webcam))
        {
            Publish(AmbientLightStatus.CameraSettling);
            Wait(RetryInterval);
            return;
        }

        if (webcam.IsInUse())
        {
            Publish(AmbientLightStatus.CameraInUse);
            Wait(RetryInterval);
            return;
        }

        var meter = webcam.TryOpenMeter(_exposureSetting, ContinuousFrameInterval);
        var streamStartTimestamp = Stopwatch.GetTimestamp();
        if (meter is null)
        {
            _watch?.Drain();
            Publish(AmbientLightStatus.Failed);
            WaitAfterFailure();
            return;
        }

        try
        {
            var stopsTotal = 0.0;
            var stopsCount = 0;
            var publishTimestamp = Stopwatch.GetTimestamp();
            var timeouts = 0;

            while (!_cancellation.IsCancellationRequested && IsStreamingWanted())
            {
                switch (meter.ReadNext(Webcam.FrameTimeout, out var reading))
                {
                    case WebcamMeterResult.Reading:
                        _exposureSetting = reading.ExposureSetting;
                        _failures = 0;
                        timeouts = 0;

                        // A frame taken while the exposure is still on its way to the band says little
                        if (meter.IsExposureSettled)
                        {
                            stopsTotal += AmbientLight.GetStops(reading.Luma, reading.Exposure);
                            stopsCount++;
                        }

                        if (
                            stopsCount > 0
                            && Stopwatch.GetElapsedTime(publishTimestamp) >= PublishInterval
                        )
                        {
                            Publish(
                                AmbientLightStatus.Read,
                                stopsTotal / stopsCount,
                                ScreenBrightness
                            );
                            stopsTotal = 0;
                            stopsCount = 0;
                            publishTimestamp = Stopwatch.GetTimestamp();
                        }

                        break;

                    case WebcamMeterResult.Timeout:
                        if (++timeouts < MaximumTimeouts)
                            break;

                        Debug.WriteLine($"'{webcam.DevicePath}' stopped delivering frames.");
                        Publish(AmbientLightStatus.Failed);
                        WaitAfterFailure();
                        return;

                    case WebcamMeterResult.Failed:
                        Publish(AmbientLightStatus.Failed);
                        WaitAfterFailure();
                        return;
                }
            }
        }
        finally
        {
            // Letting go is this program's own doing, so what the watch sees of it is not news; a
            // long stream is noted for whoever opens the camera next
            meter.Dispose();
            _watch?.Drain();
            if (Stopwatch.GetElapsedTime(streamStartTimestamp) >= LongStream)
                MarkLongStreamStopped();
        }
    }

    // Keeps off the camera while it is lent out: until a program has had it and let it go again,
    // or until nobody has taken it for a while
    [SupportedOSPlatform("linux")]
    private void Lend()
    {
        if (LookForWebcam() is not { } webcam)
        {
            Publish(AmbientLightStatus.NoCamera);
            Wait(RetryInterval);
            return;
        }

        Publish(AmbientLightStatus.CameraLent);

        var lentTimestamp = Stopwatch.GetTimestamp();
        var wasTaken = false;

        while (!_cancellation.IsCancellationRequested && IsLendingWanted())
        {
            if (_watch is not null)
                _watch.Wait(LendCheckInterval);
            else
                Pause(LendCheckInterval);

            if (webcam.IsInUse())
                wasTaken = true;
            else if (wasTaken || Stopwatch.GetElapsedTime(lentTimestamp) >= LendTimeout)
                break;

            Publish(AmbientLightStatus.CameraLent);
        }

        // The other program's stream has just stopped, if it had one
        Pause(OthersDelay);
        _watch?.Drain();
        SetCameraLent(false);
    }

    // Whether the camera has only just appeared. The system is still setting a new camera up for
    // some seconds, with udev running whatever adds Logitech's controls and sets the LED, and a
    // camera asked for a format or a setting in the middle of that can stop answering altogether.
    [SupportedOSPlatform("linux")]
    private static bool HasJustAppeared(Webcam webcam) =>
        DateTime.UtcNow - File.GetLastWriteTimeUtc(webcam.DevicePath) < AppearanceGrace;

    // Waits longer after each failure in a row, so that a camera that has stopped answering is
    // not asked again and again, each time for as long as its requests take to time out
    private void WaitAfterFailure()
    {
        _failures++;
        Wait(RetryInterval * Math.Pow(2, Math.Min(_failures, MaximumBackoffDoublings)));
    }

    // A camera that has streamed for a long while can stop answering, for good until it is
    // unplugged, if a new stream is asked of it within some seconds of the old one stopping. So the
    // moment a long stream stops is written down where the next copy of this program finds it,
    // and nothing is asked of the camera until the moment has passed.
    private static readonly string StreamStoppedMarkerPath = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } cacheDirPath
            ? cacheDirPath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache"
            ),
        "LightBulbKDE",
        "camera-stopped"
    );

    private static void MarkLongStreamStopped()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StreamStoppedMarkerPath)!);
            File.WriteAllText(StreamStoppedMarkerPath, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Failed to note when the camera's stream stopped: {ex.Message}");
        }
    }

    private void WaitAfterLongStream()
    {
        // A marker that is not there reads as very old, which is right
        var sinceStop = DateTime.UtcNow - File.GetLastWriteTimeUtc(StreamStoppedMarkerPath);
        if (sinceStop < RestartGrace)
            Pause(RestartGrace - sinceStop);
    }

    // Gives the camera a moment when another program has had it since this program last did,
    // and once at the start, since the copy of this program that just quit may have had it
    [SupportedOSPlatform("linux")]
    private void SettleAfterOthers()
    {
        var hasSettledAtStart = _hasSettledAtStart;
        _hasSettledAtStart = true;

        if (_watch is not null && _watch.Drain() != WebcamWatchEvents.None)
            Pause(OthersDelay);
        else if (!hasSettledAtStart)
            Pause(SettleDelay);
    }

    private bool IsStreamingWanted()
    {
        using (_lock.EnterScope())
        {
            return (_isMeteringRequested || _isPreviewRequested)
                && !_isCameraLent
                && !_isDarkCalibrationRequested
                && _settingsService.IsAmbientLightContinuous;
        }
    }

    private bool IsLendingWanted()
    {
        using (_lock.EnterScope())
        {
            return (_isMeteringRequested || _isPreviewRequested) && _isCameraLent;
        }
    }

    // The camera named in the settings, or the first one without a name. A named camera that is
    // not there is not stood in for, since the choice may have been about which room it looks at.
    // The camera found is watched for other programs opening and closing it from here on, and the
    // looking itself, which opens every camera for a moment, is kept out of what the watch sees.
    [SupportedOSPlatform("linux")]
    private Webcam? LookForWebcam()
    {
        var webcams = Webcam.GetAll();

        var names = webcams.Select(w => w.Name).Distinct().ToArray();
        Dispatcher.UIThread.Post(() =>
        {
            if (!Cameras.SequenceEqual(names))
                Cameras = names;
        });

        var name = _settingsService.AmbientLightCamera;

        var webcam = string.IsNullOrWhiteSpace(name)
            ? webcams.FirstOrDefault()
            : webcams.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.Ordinal));

        if (webcam is null)
            return null;

        if (_watch is null || _watchedDevicePath != webcam.DevicePath)
        {
            _watch?.Dispose();
            _watch = WebcamWatch.TryCreate(webcam.DevicePath);
            _watchedDevicePath = webcam.DevicePath;
        }
        else
        {
            _watch.Drain();
        }

        return webcam;
    }

    // Takes in a reading, if there is one, and hands the state to the UI thread, where everything
    // bound to it lives. The reading has the screen's glow taken out, then goes through the filter
    // the screen follows.
    private void Publish(
        AmbientLightStatus status,
        double? stops = null,
        double screenBrightness = 1,
        double? glowPerDimming = null
    )
    {
        var now = DateTimeOffset.Now;

        if (stops is { } measured)
        {
            var room = AmbientLight.CompensateForScreen(
                measured,
                screenBrightness,
                glowPerDimming ?? _settingsService.AmbientLightGlow
            );

            _latestStops = room;
            _lastReadingTimestamp = now;

            // A phone's sensor is answered at once when the room brightens and slowly when it
            // darkens, which keeps a passing shadow from dimming the screen
            _filteredStops = _filteredStops is { } filtered
                ? filtered + (room - filtered) * (room > filtered ? RiseRate : FallRate)
                : room;

            if (
                _followedStops is not { } followed
                || Math.Abs(_filteredStops.Value - followed) >= DeadBand
            )
                _followedStops = _filteredStops;
        }
        else if (
            status is AmbientLightStatus.NoCamera or AmbientLightStatus.Failed
            && now - _lastReadingTimestamp >= StaleAfter
        )
        {
            // Too old to say anything about the room now; a camera lent or in use keeps its last word
            _latestStops = null;
            _filteredStops = null;
            _followedStops = null;
        }

        var latestNow = _latestStops;
        var followedNow = _followedStops;

        Dispatcher.UIThread.Post(() =>
        {
            Status = status;
            LatestStops = latestNow;
            Stops = followedNow;
        });
    }

    public void Dispose()
    {
        _cancellation.Cancel();

        // A reading under way puts the camera's settings back when it finishes
        _worker?.Join(TimeSpan.FromSeconds(5));

        _cancellation.Dispose();
        _wakeSignal.Dispose();
    }
}

public partial class AmbientLightService
{
    // How fast the filtered reading closes on a new one, as the share of the distance covered per
    // reading, going up and going down
    private const double RiseRate = 0.8;
    private const double FallRate = 0.35;

    // How far the filtered reading must drift before the screen follows it
    private const double DeadBand = 0.08;

    // The brightness the screen is taken to while the room is read for its glow, and how long the
    // screen is given to get there
    private const double CalibrationBrightness = 0.6;
    private static readonly TimeSpan CalibrationDelay = TimeSpan.FromSeconds(2);

    // A stream that stays open runs at a few frames a second, which is plenty for a reading a
    // second and costs next to nothing
    private static readonly TimeSpan ContinuousFrameInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(1);
    private const int MaximumTimeouts = 2;

    // A lent camera is looked at every second, and taken back if nobody has used it in a while
    private static readonly TimeSpan LendCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LendTimeout = TimeSpan.FromMinutes(5);

    // How long the camera is left alone at the start, and after another program has had it, which
    // may have been for a long stream
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OthersDelay = TimeSpan.FromSeconds(20);

    // A stream this long is noted when it stops, and nothing opens the camera for this long after
    private static readonly TimeSpan LongStream = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RestartGrace = TimeSpan.FromSeconds(20);

    // How long a camera is left alone after it appears, and how far retries spread out after
    // failures (3 s, 6 s, 12 s, 24 s, 48 s)
    private static readonly TimeSpan AppearanceGrace = TimeSpan.FromSeconds(20);
    private const int MaximumBackoffDoublings = 4;

    // Readings come no closer than this, and no closer than this gap from the end of one to the
    // start of the next
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(5);

    // How long a reading stands for the room after the camera stops answering
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
}
