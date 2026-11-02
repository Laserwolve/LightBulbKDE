using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightBulb.Core;
using LightBulb.Core.Utils.Extensions;
using LightBulb.Framework;
using LightBulb.Localization;
using LightBulb.Models;
using LightBulb.PlatformInterop;
using LightBulb.Services;
using LightBulb.Utils.Extensions;
using PowerKit;
using PowerKit.Extensions;

namespace LightBulb.ViewModels.Components;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly GammaService _gammaService;
    private readonly AmbientLightService _ambientLightService;
    private readonly HotKeyService _hotKeyService;
    private readonly ExternalApplicationService _externalApplicationService;

    private readonly IDisposable _eventSubscription;

    private readonly Timer _updateInstantTimer;
    private readonly Timer _updateConfigurationTimer;
    private readonly Timer _updateIsPausedTimer;

    private IDisposable? _enableAfterDelayRegistration;
    private ColorConfiguration? _configurationSmoothingSource;
    private ColorConfiguration? _configurationSmoothingTarget;

    public DashboardViewModel(
        SettingsService settingsService,
        LocalizationManager localizationManager,
        GammaService gammaService,
        AmbientLightService ambientLightService,
        HotKeyService hotKeyService,
        ExternalApplicationService externalApplicationService
    )
    {
        _settingsService = settingsService;
        LocalizationManager = localizationManager;
        _gammaService = gammaService;
        _ambientLightService = ambientLightService;
        _hotKeyService = hotKeyService;
        _externalApplicationService = externalApplicationService;

        _eventSubscription = Disposable.Merge(
            // Invalidate the cached local time zone when the system clock or time zone changes
            // https://github.com/Tyrrrz/LightBulb/issues/465
            OperatingSystem.IsWindows()
                ? SystemEvent.Register(SystemEvent.Ids.TimeChanged, TimeZoneInfo.ClearCachedData)
                : Disposable.Null,
            OperatingSystem.IsWindows()
                ? SystemEvent.Register(
                    SystemEvent.Ids.SettingsChanged,
                    TimeZoneInfo.ClearCachedData
                )
                : Disposable.Null,
            this.WatchProperty(
                o => o.IsEnabled,
                v =>
                {
                    if (v)
                    {
                        // Cancel any activate 'disable temporarily' timers
                        _enableAfterDelayRegistration?.Dispose();

                        // Invalidate device contexts
                        _gammaService.InvalidateDeviceContexts();
                    }
                }
            ),
            // Refresh transition tooltips when the language changes
            localizationManager.WatchProperty(
                o => o.Language,
                _ =>
                {
                    OnPropertyChanged(nameof(SunsetTransitionTooltip));
                    OnPropertyChanged(nameof(SunriseTransitionTooltip));
                    OnPropertyChanged(nameof(AmbientLightTooltip));
                    OnPropertyChanged(nameof(RoomConfiguration));
                    OnPropertyChanged(nameof(StatusText));
                }
            ),
            // Re-register hotkeys when they get updated
            settingsService.WatchProperties(
                [
                    o => o.ToggleHotKey,
                    o => o.ToggleWindowHotKey,
                    o => o.IncreaseTemperatureOffsetHotKey,
                    o => o.DecreaseTemperatureOffsetHotKey,
                    o => o.IncreaseBrightnessOffsetHotKey,
                    o => o.DecreaseBrightnessOffsetHotKey,
                    o => o.ResetConfigurationOffsetHotKey,
                ],
                RegisterHotKeys
            ),
            // Read the room's light only while the brightness is following it
            this.WatchProperties(
                [o => o.IsEnabled, o => o.IsPaused, o => o.IsCyclePreviewEnabled],
                UpdateAmbientLightMetering
            ),
            settingsService.WatchProperty(
                o => o.IsAmbientLightEnabled,
                _ => UpdateAmbientLightMetering()
            ),
            // The tray offers to lend the camera while it is being kept on
            ambientLightService.WatchProperty(
                o => o.IsCameraLent,
                _ =>
                {
                    OnPropertyChanged(nameof(IsCameraLendable));
                    OnPropertyChanged(nameof(IsCameraLent));
                }
            ),
            settingsService.WatchProperties(
                [o => o.IsAmbientLightEnabled, o => o.IsAmbientLightContinuous],
                () => OnPropertyChanged(nameof(IsCameraLendable))
            ),
            // The screen is brightened for a moment while the room is read for its glow
            ambientLightService.WatchProperty(
                o => o.ForcedBrightness,
                _ => OnPropertyChanged(nameof(TargetConfiguration))
            ),
            // Follow each reading of the room's light
            ambientLightService.WatchProperty(
                o => o.Stops,
                _ =>
                {
                    OnPropertyChanged(nameof(AmbientLightLevel));
                    OnPropertyChanged(nameof(IsAmbientLightActive));
                    OnPropertyChanged(nameof(AmbientLightTooltip));
                    OnPropertyChanged(nameof(RoomConfiguration));
                    OnPropertyChanged(nameof(TargetConfiguration));
                    OnPropertyChanged(nameof(CycleState));
                }
            )
        );

        _updateConfigurationTimer = new Timer(
            TimeSpan.FromMilliseconds(50),
            () => Dispatcher.UIThread.Post(UpdateConfiguration)
        );
        _updateInstantTimer = new Timer(
            TimeSpan.FromMilliseconds(50),
            () => Dispatcher.UIThread.Post(UpdateInstant)
        );
        _updateIsPausedTimer = new Timer(
            TimeSpan.FromSeconds(1),
            () => Dispatcher.UIThread.Post(UpdateIsPaused)
        );
    }

    public LocalizationManager LocalizationManager { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(AmbientLightLevel))]
    [NotifyPropertyChangedFor(nameof(IsAmbientLightActive))]
    [NotifyPropertyChangedFor(nameof(IsFollowingRoom))]
    [NotifyPropertyChangedFor(nameof(IsCameraLendable))]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(AmbientLightLevel))]
    [NotifyPropertyChangedFor(nameof(IsAmbientLightActive))]
    [NotifyPropertyChangedFor(nameof(IsFollowingRoom))]
    [NotifyPropertyChangedFor(nameof(IsCameraLendable))]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(AmbientLightLevel))]
    [NotifyPropertyChangedFor(nameof(IsAmbientLightActive))]
    [NotifyPropertyChangedFor(nameof(IsFollowingRoom))]
    [NotifyPropertyChangedFor(nameof(IsCameraLendable))]
    public partial bool IsCyclePreviewEnabled { get; set; }

    public bool IsActive => IsEnabled && !IsPaused || IsCyclePreviewEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SolarTimes))]
    [NotifyPropertyChangedFor(nameof(SunriseStart))]
    [NotifyPropertyChangedFor(nameof(SunriseEnd))]
    [NotifyPropertyChangedFor(nameof(SunsetStart))]
    [NotifyPropertyChangedFor(nameof(SunsetEnd))]
    [NotifyPropertyChangedFor(nameof(SunriseTransitionTooltip))]
    [NotifyPropertyChangedFor(nameof(SunsetTransitionTooltip))]
    [NotifyPropertyChangedFor(nameof(CycleConfiguration))]
    [NotifyPropertyChangedFor(nameof(RoomConfiguration))]
    [NotifyPropertyChangedFor(nameof(TargetConfiguration))]
    [NotifyPropertyChangedFor(nameof(CycleState))]
    public partial DateTimeOffset Instant { get; set; } = DateTimeOffset.Now;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffsetEnabled))]
    [NotifyPropertyChangedFor(nameof(CycleConfiguration))]
    [NotifyPropertyChangedFor(nameof(RoomConfiguration))]
    [NotifyPropertyChangedFor(nameof(TargetConfiguration))]
    [NotifyPropertyChangedFor(nameof(AdjustedDayConfiguration))]
    [NotifyPropertyChangedFor(nameof(AdjustedNightConfiguration))]
    [NotifyPropertyChangedFor(nameof(CycleState))]
    public partial double TemperatureOffset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffsetEnabled))]
    [NotifyPropertyChangedFor(nameof(CycleConfiguration))]
    [NotifyPropertyChangedFor(nameof(RoomConfiguration))]
    [NotifyPropertyChangedFor(nameof(TargetConfiguration))]
    [NotifyPropertyChangedFor(nameof(AdjustedDayConfiguration))]
    [NotifyPropertyChangedFor(nameof(AdjustedNightConfiguration))]
    [NotifyPropertyChangedFor(nameof(CycleState))]
    public partial double BrightnessOffset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial ColorConfiguration CurrentConfiguration { get; set; } =
        ColorConfiguration.Default;

    public SolarTimes SolarTimes =>
        _settingsService is { IsManualSunriseSunsetEnabled: false, Location: { } location }
            ? SolarTimes.Calculate(location, Instant)
            : new SolarTimes(_settingsService.ManualSunrise, _settingsService.ManualSunset);

    public TimeOnly SunriseStart =>
        Cycle.GetSunriseStart(
            SolarTimes.Sunrise,
            _settingsService.ConfigurationTransitionDuration,
            _settingsService.ConfigurationTransitionOffset
        );

    public TimeOnly SunriseEnd =>
        Cycle.GetSunriseEnd(
            SolarTimes.Sunrise,
            _settingsService.ConfigurationTransitionDuration,
            _settingsService.ConfigurationTransitionOffset
        );

    public TimeOnly SunsetStart =>
        Cycle.GetSunsetStart(
            SolarTimes.Sunset,
            _settingsService.ConfigurationTransitionDuration,
            _settingsService.ConfigurationTransitionOffset
        );

    public TimeOnly SunsetEnd =>
        Cycle.GetSunsetEnd(
            SolarTimes.Sunset,
            _settingsService.ConfigurationTransitionDuration,
            _settingsService.ConfigurationTransitionOffset
        );

    public string SunsetTransitionTooltip =>
        string.Format(
            LocalizationManager.SunsetTransitionTooltip,
            SunsetStart.ToString(CultureInfo.CurrentCulture),
            SunsetEnd.ToString(CultureInfo.CurrentCulture)
        );

    public string SunriseTransitionTooltip =>
        string.Format(
            LocalizationManager.SunriseTransitionTooltip,
            SunriseStart.ToString(CultureInfo.CurrentCulture),
            SunriseEnd.ToString(CultureInfo.CurrentCulture)
        );

    public bool IsOffsetEnabled => Math.Abs(TemperatureOffset) + Math.Abs(BrightnessOffset) >= 0.01;

    // What the day and night cycle alone asks for at this instant
    public ColorConfiguration CycleConfiguration =>
        IsActive
            ? Cycle
                .InterpolateConfiguration(
                    SolarTimes,
                    _settingsService.DayConfiguration,
                    _settingsService.NightConfiguration,
                    _settingsService.ConfigurationTransitionDuration,
                    _settingsService.ConfigurationTransitionOffset,
                    Instant
                )
                .WithOffset(TemperatureOffset, BrightnessOffset)
                .Clamp(
                    _settingsService.MinimumTemperature,
                    _settingsService.MaximumTemperature,
                    _settingsService.MinimumBrightness,
                    _settingsService.MaximumBrightness
                )
        : _settingsService.IsDefaultToDayConfigurationEnabled ? _settingsService.DayConfiguration
        : ColorConfiguration.Default;

    // Whether the screen follows the room rather than the clock, which it does whenever the room
    // is being read. The cycle preview shows the clock's own cycle, so the room stays out of it.
    public bool IsFollowingRoom =>
        IsEnabled && !IsPaused && !IsCyclePreviewEnabled && _settingsService.IsAmbientLightEnabled;

    // The light in the room, from 0 in a room as dark as the dark reference to 1 in one as bright
    // as the bright reference, while the screen follows the room and a reading has come in
    public double? AmbientLightLevel =>
        IsFollowingRoom && _ambientLightService.Stops is { } stops
            ? AmbientLight.GetLevel(
                stops,
                _settingsService.AmbientLightDarkStops,
                _settingsService.AmbientLightBrightStops
            )
            : null;

    public bool IsAmbientLightActive => AmbientLightLevel is not null;

    // Whether the camera is being read for the room's light, so that it could be lent out
    public bool IsCameraLendable => IsFollowingRoom && !_ambientLightService.IsCameraLent;

    public bool IsCameraLent => _ambientLightService.IsCameraLent;

    public string AmbientLightTooltip =>
        string.Format(
            LocalizationManager.AmbientLightIndicatorTooltip,
            (AmbientLightLevel ?? 0).ToString("P0", CultureInfo.CurrentCulture)
        );

    // What the room asks for: the night-time settings in a room as dark as the dark reference, the
    // day-time settings in one as bright as the bright reference, and in between for anything
    // between, with the offset on top. Until the first reading comes in, the room counts as bright.
    public ColorConfiguration RoomConfiguration =>
        AmbientLight
            .GetConfiguration(
                AmbientLightLevel ?? 1,
                _settingsService.NightConfiguration,
                _settingsService.DayConfiguration
            )
            .WithOffset(TemperatureOffset, BrightnessOffset)
            .Clamp(
                _settingsService.MinimumTemperature,
                _settingsService.MaximumTemperature,
                _settingsService.MinimumBrightness,
                _settingsService.MaximumBrightness
            );

    public ColorConfiguration TargetConfiguration
    {
        get
        {
            if (!IsActive)
            {
                return _settingsService.IsDefaultToDayConfigurationEnabled
                    ? _settingsService.DayConfiguration
                    : ColorConfiguration.Default;
            }

            if (!IsFollowingRoom)
                return CycleConfiguration;

            // While the room is read for the screen's own glow, the screen holds the brightness asked
            if (_ambientLightService.ForcedBrightness is { } forcedBrightness)
            {
                return RoomConfiguration with
                {
                    Brightness = Math.Clamp(
                        forcedBrightness,
                        _settingsService.MinimumBrightness,
                        _settingsService.MaximumBrightness
                    ),
                };
            }

            return RoomConfiguration;
        }
    }

    public ColorConfiguration AdjustedDayConfiguration =>
        _settingsService.DayConfiguration.WithOffset(TemperatureOffset, BrightnessOffset);

    public ColorConfiguration AdjustedNightConfiguration =>
        _settingsService.NightConfiguration.WithOffset(TemperatureOffset, BrightnessOffset);

    public CycleState CycleState =>
        this switch
        {
            _ when CurrentConfiguration != TargetConfiguration => CycleState.Transition,
            _ when !IsEnabled => CycleState.Disabled,
            _ when IsPaused => CycleState.Paused,
            // A room between dark and bright is a transition, like a sunset
            _ when TargetConfiguration == AdjustedDayConfiguration => CycleState.Day,
            _ when TargetConfiguration == AdjustedNightConfiguration => CycleState.Night,
            _ => CycleState.Transition,
        };

    public string StatusText =>
        Program.Name
        + Environment.NewLine
        + (
            IsActive
                ? CurrentConfiguration.Temperature.ToString("F0")
                    + " / "
                    + CurrentConfiguration.Brightness.ToString("P0")
                : LocalizationManager.TrayTooltipDisabled
        );

    private void RegisterHotKeys()
    {
        _hotKeyService.UnregisterAllHotKeys();

        if (_settingsService.ToggleHotKey != HotKey.None)
        {
            _hotKeyService.RegisterHotKey(
                _settingsService.ToggleHotKey,
                () => IsEnabled = !IsEnabled
            );
        }

        if (_settingsService.ToggleWindowHotKey != HotKey.None)
        {
            _hotKeyService.RegisterHotKey(
                _settingsService.ToggleWindowHotKey,
                () => App.Current?.ToggleMainWindow()
            );
        }

        if (_settingsService.IncreaseTemperatureOffsetHotKey != HotKey.None)
        {
            _hotKeyService.RegisterHotKey(
                _settingsService.IncreaseTemperatureOffsetHotKey,
                () =>
                {
                    TemperatureOffset += Math.Min(
                        100,
                        _settingsService.MaximumTemperature - TargetConfiguration.Temperature
                    );
                }
            );
        }

        if (_settingsService.DecreaseTemperatureOffsetHotKey != HotKey.None)
        {
            _hotKeyService.RegisterHotKey(
                _settingsService.DecreaseTemperatureOffsetHotKey,
                () =>
                {
                    TemperatureOffset += Math.Max(
                        -100,
                        _settingsService.MinimumTemperature - TargetConfiguration.Temperature
                    );
                }
            );
        }

        if (_settingsService.IncreaseBrightnessOffsetHotKey != HotKey.None)
        {
            _hotKeyService.RegisterHotKey(
                _settingsService.IncreaseBrightnessOffsetHotKey,
                () =>
                {
                    BrightnessOffset += Math.Min(
                        0.05,
                        _settingsService.MaximumBrightness - TargetConfiguration.Brightness
                    );
                }
            );
        }

        if (_settingsService.DecreaseBrightnessOffsetHotKey != HotKey.None)
        {
            _hotKeyService.RegisterHotKey(
                _settingsService.DecreaseBrightnessOffsetHotKey,
                () =>
                {
                    BrightnessOffset += Math.Max(
                        -0.05,
                        _settingsService.MinimumBrightness - TargetConfiguration.Brightness
                    );
                }
            );
        }

        if (_settingsService.ResetConfigurationOffsetHotKey != HotKey.None)
        {
            _hotKeyService.RegisterHotKey(
                _settingsService.ResetConfigurationOffsetHotKey,
                ResetConfigurationOffset
            );
        }
    }

    // The camera is read only while the brightness is following the room
    private void UpdateAmbientLightMetering() =>
        _ambientLightService.IsMeteringRequested =
            IsEnabled
            && !IsPaused
            && !IsCyclePreviewEnabled
            && _settingsService.IsAmbientLightEnabled;

    private void UpdateInstant()
    {
        // If in cycle preview mode, advance quickly until the full cycle has been reached
        if (IsCyclePreviewEnabled)
        {
            // Cycle is supposed to end 1 full day past the current real time
            var targetInstant = DateTimeOffset.Now + TimeSpan.FromDays(1);

            Instant = Instant.StepTo(targetInstant, TimeSpan.FromMinutes(5));
            if (Instant >= targetInstant)
                IsCyclePreviewEnabled = false;
        }
        // Otherwise, synchronize the instant with the system clock
        else
        {
            Instant = DateTimeOffset.Now;
        }
    }

    private void UpdateConfiguration()
    {
        var isSmooth =
            !IsCyclePreviewEnabled
            && CurrentConfiguration != TargetConfiguration
            && _settingsService.IsConfigurationSmoothingEnabled
            && _settingsService.ConfigurationSmoothingMaxDuration.TotalSeconds >= 0.1;

        if (isSmooth)
        {
            // Check if the target configuration has changed since the last transition started
            if (
                _configurationSmoothingTarget != TargetConfiguration
                || _configurationSmoothingSource is null
            )
            {
                _configurationSmoothingSource = CurrentConfiguration;
                _configurationSmoothingTarget = TargetConfiguration;
            }

            var brightnessDelta = Math.Abs(
                _configurationSmoothingTarget.Value.Brightness
                    - _configurationSmoothingSource.Value.Brightness
            );

            var brightnessStep = Math.Max(
                brightnessDelta
                    / _settingsService.ConfigurationSmoothingMaxDuration.TotalSeconds
                    * _updateConfigurationTimer.Interval.TotalSeconds,
                0.08
            );

            var temperatureDelta = Math.Abs(
                _configurationSmoothingTarget.Value.Temperature
                    - _configurationSmoothingSource.Value.Temperature
            );

            var temperatureStep = Math.Max(
                temperatureDelta
                    / _settingsService.ConfigurationSmoothingMaxDuration.TotalSeconds
                    * _updateConfigurationTimer.Interval.TotalSeconds,
                30
            );

            CurrentConfiguration = CurrentConfiguration.StepTo(
                TargetConfiguration,
                temperatureStep,
                brightnessStep
            );
        }
        else
        {
            CurrentConfiguration = TargetConfiguration;
            _configurationSmoothingSource = null;
            _configurationSmoothingTarget = null;
        }

        _gammaService.SetGamma(CurrentConfiguration);

        // The room is read with the screen's own glow in it, which this lets the reading allow for
        _ambientLightService.ScreenBrightness = CurrentConfiguration.Brightness;
    }

    private void UpdateIsPaused()
    {
        // Detecting fullscreen and whitelisted applications reads Windows' window list, so
        // elsewhere only the desktop's own switch can pause LightBulb
        if (!OperatingSystem.IsWindows())
        {
            IsPaused = _gammaService.IsPausedExternally;
            return;
        }

        bool IsPausedByFullScreen() =>
            _settingsService.IsPauseWhenFullScreenEnabled
            && _externalApplicationService.IsForegroundApplicationFullScreen();

        bool IsPausedByWhitelistedApplication() =>
            _settingsService.IsApplicationWhitelistEnabled
            && _settingsService.WhitelistedApplications is not null
            && _settingsService.WhitelistedApplications.Contains(
                _externalApplicationService.TryGetForegroundApplication()
            );

        IsPaused = IsPausedByFullScreen() || IsPausedByWhitelistedApplication();
    }

    public override Task InitializeAsync()
    {
        _updateInstantTimer.Start();
        _updateConfigurationTimer.Start();
        _updateIsPausedTimer.Start();

        UpdateAmbientLightMetering();

        // Hack: feign property changes to refresh the tray icon
        OnAllPropertiesChanged();

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void DisableTemporarily(TimeSpan duration)
    {
        IsEnabled = false;
        _enableAfterDelayRegistration?.Dispose();
        _enableAfterDelayRegistration = Timer.QueueDelayedAction(
            duration,
            () => Dispatcher.UIThread.Post(() => IsEnabled = true)
        );
    }

    [RelayCommand]
    private void DisableUntilSunrise()
    {
        var now = DateTimeOffset.Now;
        var timeUntilSunrise = SolarTimes.Sunrise.NextAfter(now) - now;
        DisableTemporarily(timeUntilSunrise);
    }

    [RelayCommand]
    private void Toggle() => IsEnabled = !IsEnabled;

    [RelayCommand]
    private void LendCamera() => _ambientLightService.LendCamera();

    [RelayCommand]
    private void TakeCameraBack() => _ambientLightService.TakeCameraBack();

    [RelayCommand]
    private void ResetConfigurationOffset()
    {
        TemperatureOffset = 0;
        BrightnessOffset = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _eventSubscription.Dispose();

            _updateInstantTimer.Dispose();
            _updateConfigurationTimer.Dispose();
            _updateIsPausedTimer.Dispose();

            _enableAfterDelayRegistration?.Dispose();
        }

        base.Dispose(disposing);
    }
}
