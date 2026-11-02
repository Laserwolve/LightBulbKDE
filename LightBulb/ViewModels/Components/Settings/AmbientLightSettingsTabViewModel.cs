using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightBulb.Core;
using LightBulb.Localization;
using LightBulb.Services;
using PowerKit;
using PowerKit.Extensions;

namespace LightBulb.ViewModels.Components.Settings;

public partial class AmbientLightSettingsTabViewModel : SettingsTabViewModelBase
{
    private readonly AmbientLightService _ambientLightService;
    private readonly IDisposable _eventSubscription;

    public AmbientLightSettingsTabViewModel(
        SettingsService settingsService,
        LocalizationManager localizationManager,
        AmbientLightService ambientLightService
    )
        : base(settingsService, localizationManager, 2)
    {
        _ambientLightService = ambientLightService;

        _eventSubscription = Disposable.Merge(
            // The camera is read every few seconds while the tab is on screen, so that the
            // reading can be watched while the room is set up
            this.WatchProperty(
                o => o.IsShown,
                v =>
                {
                    _ambientLightService.IsPreviewRequested = v;
                    if (v)
                        _ambientLightService.RequestCameras();
                }
            ),
            ambientLightService.WatchProperty(
                o => o.Cameras,
                _ =>
                {
                    OnPropertyChanged(nameof(AvailableCameras));
                    OnPropertyChanged(nameof(Camera));
                }
            ),
            ambientLightService.WatchProperties(
                [o => o.LatestStops, o => o.Status],
                () =>
                {
                    OnPropertyChanged(nameof(Level));
                    OnPropertyChanged(nameof(HasReading));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(IsStatusShown));
                    SetDarkReferenceCommand.NotifyCanExecuteChanged();
                    SetBrightReferenceCommand.NotifyCanExecuteChanged();
                }
            )
        );
    }

    public override string DisplayName => LocalizationManager.AmbientLightTabName;

    // Whether the tab is on screen, which its view reports
    [ObservableProperty]
    public partial bool IsShown { get; set; }

    public bool IsAmbientLightEnabled
    {
        get => SettingsService.IsAmbientLightEnabled;
        set => SettingsService.IsAmbientLightEnabled = value;
    }

    public bool IsContinuous
    {
        get => SettingsService.IsAmbientLightContinuous;
        set => SettingsService.IsAmbientLightContinuous = value;
    }

    public IReadOnlyList<string> AvailableCameras => _ambientLightService.Cameras;

    // The camera's name; without a choice, the first camera found
    public string? Camera
    {
        get => SettingsService.AmbientLightCamera ?? AvailableCameras.FirstOrDefault();
        set => SettingsService.AmbientLightCamera = value;
    }

    public TimeSpan Interval
    {
        get => SettingsService.AmbientLightInterval;
        set =>
            SettingsService.AmbientLightInterval = TimeSpan.FromSeconds(
                value.TotalSeconds.Clamp(2, 300)
            );
    }

    // The latest reading, from 0 at the dark reference to 1 at the bright one
    public double? Level =>
        _ambientLightService.LatestStops is { } stops
            ? AmbientLight.GetLevel(
                stops,
                SettingsService.AmbientLightDarkStops,
                SettingsService.AmbientLightBrightStops
            )
            : null;

    public bool HasReading => Level is not null;

    public string? StatusText =>
        _ambientLightService.Status switch
        {
            AmbientLightStatus.Idle => LocalizationManager.AmbientLightIdleText,
            AmbientLightStatus.NoCamera => LocalizationManager.AmbientLightNoCameraText,
            AmbientLightStatus.CameraInUse => LocalizationManager.AmbientLightCameraInUseText,
            AmbientLightStatus.CameraLent => LocalizationManager.AmbientLightCameraLentText,
            AmbientLightStatus.CameraSettling => LocalizationManager.AmbientLightCameraSettlingText,
            AmbientLightStatus.Calibrating => LocalizationManager.AmbientLightCalibratingText,
            AmbientLightStatus.Failed => LocalizationManager.AmbientLightFailedText,
            _ => null,
        };

    public bool IsStatusShown => StatusText is not null;

    private bool CanSetReference() => _ambientLightService.LatestStops is not null;

    // Takes the room as it is now for the darkest it gets, measuring the screen's own glow on the way
    [RelayCommand(CanExecute = nameof(CanSetReference))]
    private void SetDarkReference() => _ambientLightService.CalibrateDarkReference();

    // Takes the room as it is now for the brightest it gets
    [RelayCommand(CanExecute = nameof(CanSetReference))]
    private void SetBrightReference()
    {
        if (_ambientLightService.LatestStops is { } stops)
            SettingsService.AmbientLightBrightStops = stops;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _eventSubscription.Dispose();
            _ambientLightService.IsPreviewRequested = false;
        }

        base.Dispose(disposing);
    }
}
