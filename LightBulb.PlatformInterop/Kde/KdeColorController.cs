using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace LightBulb.PlatformInterop.Kde;

// KWin composites the screen on KDE Plasma, so no other program can load a gamma ramp the way
// LightBulb does on Windows. KWin has two controls that do the same job between them instead:
// Night Light, held in its "always on" mode at whatever temperature LightBulb wants, and the
// dimming multiplier each output has on top of its brightness slider. Both are set from a
// background worker, since each change runs a KDE command-line tool.
[SupportedOSPlatform("linux")]
public partial class KdeColorController : IDisposable
{
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;

    // Set by the app, read by the worker
    private (int Temperature, int Dimming)? _requested;
    private bool _isVerificationRequested;
    private DateTimeOffset _dimmingWatchEnd = DateTimeOffset.MinValue;

    // Owned by the worker
    private int? _appliedTemperature;
    private int? _appliedDimming;
    private IReadOnlyList<string> _outputNames = [];
    private DateTimeOffset _lastVerificationTimestamp = DateTimeOffset.MinValue;

    private volatile bool _isNightLightSuspended;

    public KdeColorController() => _worker = Task.Run(RunAsync);

    // Night Light's own Suspend switch in Plasma's Brightness and Color applet
    public bool IsNightLightSuspended => _isNightLightSuspended;

    public void SetConfiguration(double temperature, double brightness)
    {
        using (_lock.EnterScope())
            _requested = (ToNightLightTemperature(temperature), ToDimmingPercent(brightness));

        Wake();
    }

    // Checks KWin's actual state against what was asked for on the next update
    public void Refresh()
    {
        using (_lock.EnterScope())
            _isVerificationRequested = true;

        Wake();
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

    private void OnBrightnessChanged()
    {
        // PowerDevil sends its own dimming value, normally 100%, along with every brightness
        // change, which undoes ours. Its change lands a moment after this signal, so the
        // dimming is watched closely for a little while and put back as soon as it moves.
        using (_lock.EnterScope())
            _dimmingWatchEnd = DateTimeOffset.Now + DimmingWatchDuration;

        Wake();
    }

    private async Task RunAsync()
    {
        using var watch = await KdeBus.WatchAsync(OnBrightnessChanged, Wake);

        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                bool isWatchingDimming;
                using (_lock.EnterScope())
                    isWatchingDimming = DateTimeOffset.Now < _dimmingWatchEnd;

                await _wakeSignal.WaitAsync(
                    isWatchingDimming ? DimmingWatchInterval : TickInterval,
                    _cancellation.Token
                );

                await UpdateAsync();

                // Lets a burst of requests, such as LightBulb's smoothing ticks, collapse into one
                // write, and leaves room for KWin's own fade between temperatures. While dimming is
                // being watched, every moment it stays undone shows as a flash, so there is no pause.
                if (!isWatchingDimming)
                    await Task.Delay(MinimumUpdateSpacing, _cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update KDE color settings: {ex}");
            }
        }
    }

    private async Task UpdateAsync()
    {
        (int Temperature, int Dimming)? requested;
        bool isVerificationRequested;
        bool isWatchingDimming;

        using (_lock.EnterScope())
        {
            requested = _requested;
            isVerificationRequested = _isVerificationRequested;
            isWatchingDimming = DateTimeOffset.Now < _dimmingWatchEnd;
            _isVerificationRequested = false;
        }

        // Read on every tick, to follow the Suspend switch and anything else changing Night Light
        var state = await KdeBus.TryGetNightLightStateAsync();
        if (state is not null)
            _isNightLightSuspended = state.Value.IsInhibited;

        // Nothing has been asked for yet
        if (requested is not { } target)
            return;

        var instant = DateTimeOffset.Now;
        var isVerificationDue =
            isVerificationRequested
            || (instant - _lastVerificationTimestamp).Duration() >= VerificationInterval;

        if (isVerificationDue)
            _lastVerificationTimestamp = instant;

        UpdateTemperature(target.Temperature, state, isVerificationDue);

        // Night Light's Suspend switch lifts the dimming through LightBulb itself, which pauses
        // on it and fades to the day settings, in step with Night Light's own fade
        UpdateDimming(target.Dimming, isVerificationDue, isVerificationDue || isWatchingDimming);
    }

    private void UpdateTemperature(int temperature, NightLightState? state, bool isVerificationDue)
    {
        // Night Light only holds a set temperature while it is on and in its "always on" mode,
        // which is made so when LightBulb first takes it over and whenever a check finds otherwise
        var isTakeOverNeeded =
            _appliedTemperature is null
            || isVerificationDue && state is { IsEnabled: false } or { IsConstantMode: false };

        // Something else changed the temperature, such as Night Light's settings page
        var isDrifted =
            isVerificationDue
            && state
                is {
                    IsEnabled: true,
                    IsRunning: true,
                    IsConstantMode: true,
                    IsInhibited: false,
                } actualState
            && actualState.TargetTemperature != temperature;

        if (!isTakeOverNeeded && !isDrifted && temperature == _appliedTemperature)
            return;

        // The temperature goes first, so that switching modes fades straight to it
        var isApplied = KdeCli.TryWriteKWinConfig(
            "NightColor",
            "NightTemperature",
            temperature.ToString(CultureInfo.InvariantCulture)
        );

        if (isTakeOverNeeded)
        {
            isApplied &= KdeCli.TryWriteKWinConfig("NightColor", "Mode", "Constant");
            isApplied &= KdeCli.TryWriteKWinConfig("NightColor", "Active", "true");
        }

        _appliedTemperature = isApplied ? temperature : null;
        Debug.WriteLine($"Set Night Light to {temperature}K.");
    }

    private void UpdateDimming(int dimming, bool isOutputRefreshDue, bool isDimmingCheckDue)
    {
        var isStale = _appliedDimming is null;

        // Outputs come and go with hotplugging and display configuration changes
        if (isStale || isOutputRefreshDue)
        {
            if (KdeCli.TryGetEnabledOutputNames() is { } outputNames)
            {
                isStale |= !outputNames.SequenceEqual(_outputNames);
                _outputNames = outputNames;
            }
        }

        if (_outputNames.Count == 0)
            return;

        // Anything else that sets dimming, PowerDevil above all, is put right
        if (!isStale && isDimmingCheckDue && KdeCli.TryGetOutputDimmings() is { } dimmings)
        {
            isStale = _outputNames.Any(name =>
                !dimmings.TryGetValue(name, out var actualDimming) || actualDimming != dimming
            );
        }

        if (!isStale && dimming == _appliedDimming)
            return;

        _appliedDimming = KdeCli.TrySetOutputDimming(_outputNames, dimming) ? dimming : null;
        Debug.WriteLine($"Set dimming to {dimming}% on {string.Join(", ", _outputNames)}.");
    }

    public void Dispose()
    {
        _cancellation.Cancel();

        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The worker logs its own failures
        }

        // Leave the screen untinted and undimmed, as LightBulb does on Windows when it exits. This
        // is done whenever anything was asked for, since a failed write may still have half landed.
        bool isRequested;
        using (_lock.EnterScope())
            isRequested = _requested is not null;

        if (isRequested)
        {
            KdeCli.TryWriteKWinConfig(
                "NightColor",
                "NightTemperature",
                ((int)MaximumTemperature).ToString(CultureInfo.InvariantCulture)
            );

            KdeCli.TrySetOutputDimming(
                KdeCli.TryGetEnabledOutputNames() ?? _outputNames,
                NeutralDimming
            );
        }

        _cancellation.Dispose();
        _wakeSignal.Dispose();
    }
}

public partial class KdeColorController
{
    // Night Light only ever warms the screen: 6500K is neutral and the most it accepts, and 1000K
    // the least. It fades in 50K steps itself, so finer values would only add config writes.
    public const double MinimumTemperature = 1000;
    public const double MaximumTemperature = 6500;
    private const int TemperatureStep = 50;

    private const int NeutralDimming = 100;

    // LightBulb's brightness scales gamma-encoded pixel values, which takes light down by about
    // its 2.2nd power, while KWin's dimming scales light directly. Converting between them keeps
    // a brightness setting looking the way it does on Windows.
    private const double DisplayGamma = 2.2;

    // How often the worker runs unasked, mainly to follow Night Light's Suspend switch
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    // How often KWin's actual state is checked against the requested one
    private static readonly TimeSpan VerificationInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MinimumUpdateSpacing = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan DimmingWatchDuration = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DimmingWatchInterval = TimeSpan.FromMilliseconds(50);

    public static bool IsSupported { get; } =
        KdeCli.IsAvailable("kwriteconfig6") && KdeCli.IsAvailable("kscreen-doctor");

    internal static int ToNightLightTemperature(double temperature) =>
        (int)(
            Math.Round(
                Math.Clamp(temperature, MinimumTemperature, MaximumTemperature) / TemperatureStep
            ) * TemperatureStep
        );

    // A whole percentage, as kscreen-doctor takes it. The floor is 1% so that rounding never asks
    // for KWin's darkest setting.
    internal static int ToDimmingPercent(double brightness) =>
        (int)
            Math.Clamp(
                Math.Round(Math.Pow(Math.Clamp(brightness, 0, 1), DisplayGamma) * 100),
                1,
                100
            );
}
