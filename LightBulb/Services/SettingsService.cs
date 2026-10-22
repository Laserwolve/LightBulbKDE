using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using Cogwheel;
using CommunityToolkit.Mvvm.ComponentModel;
using LightBulb.Core;
using LightBulb.Framework;
using LightBulb.Localization;
using LightBulb.Models;
using LightBulb.PlatformInterop;
using LightBulb.PlatformInterop.Kde;
using Microsoft.Win32;

namespace LightBulb.Services;

[ObservableObject]
public partial class SettingsService()
    : SettingsBase(StartOptions.Current.SettingsPath, SerializerContext.Default)
{
    private readonly RegistrySwitch<int>? _extendedGammaRangeSwitch = OperatingSystem.IsWindows()
        ? new(
            RegistryHive.LocalMachine,
            @"Software\Microsoft\Windows NT\CurrentVersion\ICM",
            "GdiICMGammaRange",
            256
        )
        : null;

    private readonly RegistrySwitch<string>? _autoStartSwitch = OperatingSystem.IsWindows()
        ? new(
            RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            Program.Name,
            $"\"{Program.ExecutableFilePath}\" {StartOptions.IsInitiallyHiddenArgument}"
        )
        : null;

    private readonly XdgAutoStartSwitch? _xdgAutoStartSwitch = OperatingSystem.IsLinux()
        ? new(Program.Name, Program.ExecutableFilePath, StartOptions.IsInitiallyHiddenArgument)
        : null;

    [ObservableProperty]
    public partial bool IsFirstTimeExperienceEnabled { get; set; } = true;

    [ObservableProperty]
    [JsonIgnore] // comes from registry
    public partial bool IsExtendedGammaRangeUnlocked { get; set; }

    // General

    // On KDE Plasma, Night Light does the tinting and only goes from 1000K up to neutral
    public double MinimumTemperature =>
        OperatingSystem.IsLinux() ? KdeColorController.MinimumTemperature : 500;

    public double MaximumTemperature =>
        OperatingSystem.IsLinux() ? KdeColorController.MaximumTemperature : 20_000;

    public double MinimumBrightness => 0.1;

    public double MaximumBrightness => 1;

    [ObservableProperty]
    public partial ColorConfiguration DayConfiguration { get; set; } = new(6600, 1);

    [ObservableProperty]
    public partial ColorConfiguration NightConfiguration { get; set; } = new(3900, 0.85);

    [ObservableProperty]
    public partial TimeSpan ConfigurationTransitionDuration { get; set; } =
        TimeSpan.FromMinutes(40);

    [ObservableProperty]
    public partial double ConfigurationTransitionOffset { get; set; }

    [ObservableProperty]
    public partial TimeSpan ConfigurationSmoothingMaxDuration { get; set; } =
        TimeSpan.FromSeconds(5);

    // Location

    [ObservableProperty]
    public partial bool IsManualSunriseSunsetEnabled { get; set; } = true;

    [ObservableProperty]
    [JsonPropertyName("ManualSunriseTime")]
    public partial TimeOnly ManualSunrise { get; set; } = new(07, 20);

    [ObservableProperty]
    [JsonPropertyName("ManualSunsetTime")]
    public partial TimeOnly ManualSunset { get; set; } = new(16, 30);

    [ObservableProperty]
    public partial GeoLocation? Location { get; set; }

    // Advanced

    [ObservableProperty]
    public partial ThemeVariant Theme { get; set; }

    [ObservableProperty]
    public partial Language Language { get; set; }

    [ObservableProperty]
    [JsonIgnore] // comes from registry
    public partial bool IsAutoStartEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsAutoUpdateEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDefaultToDayConfigurationEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsConfigurationSmoothingEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsPauseWhenFullScreenEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsGammaPollingEnabled { get; set; }

    // Application whitelist

    [ObservableProperty]
    public partial bool IsApplicationWhitelistEnabled { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ExternalApplication>? WhitelistedApplications { get; set; }

    // HotKeys

    [ObservableProperty]
    public partial HotKey ToggleHotKey { get; set; }

    [ObservableProperty]
    [JsonPropertyName("FocusWindowHotKey")]
    public partial HotKey ToggleWindowHotKey { get; set; }

    [ObservableProperty]
    public partial HotKey IncreaseTemperatureOffsetHotKey { get; set; }

    [ObservableProperty]
    public partial HotKey DecreaseTemperatureOffsetHotKey { get; set; }

    [ObservableProperty]
    public partial HotKey IncreaseBrightnessOffsetHotKey { get; set; }

    [ObservableProperty]
    public partial HotKey DecreaseBrightnessOffsetHotKey { get; set; }

    [ObservableProperty]
    public partial HotKey ResetConfigurationOffsetHotKey { get; set; }

    public override void Reset()
    {
        base.Reset();

        // Don't reset the first-time experience
        IsFirstTimeExperienceEnabled = false;

        // Trigger UI updates
        OnPropertyChanged(string.Empty);
    }

    public override void Save()
    {
        // Disallow auto-start in debug mode to make things simpler
#if DEBUG
        IsAutoStartEnabled = false;
#endif

        base.Save();

        // Update values in the registry
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _extendedGammaRangeSwitch?.IsSet = IsExtendedGammaRangeUnlocked;
                _autoStartSwitch?.IsSet = IsAutoStartEnabled;
            }
            catch (Win32Exception)
            {
                // This can happen if the user doesn't have the necessary permissions to update
                // the corresponding registry keys, and privilege elevation has failed.
                // Throwing an exception here is very messy, so we'll just ignore it.
                // https://github.com/Tyrrrz/LightBulb/issues/335
            }
        }
        // Update the autostart entry
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                _xdgAutoStartSwitch?.IsSet = IsAutoStartEnabled;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Same as above: not worth failing the whole save over
            }
        }

        // Trigger UI updates
        OnPropertyChanged(string.Empty);
    }

    public override bool Load()
    {
        var wasLoaded = base.Load();

        // Get values from the registry
        if (OperatingSystem.IsWindows())
        {
            IsExtendedGammaRangeUnlocked = _extendedGammaRangeSwitch?.IsSet == true;
            IsAutoStartEnabled = _autoStartSwitch?.IsSet == true;
        }
        else
        {
            // The gamma range limit is a Windows setting, so there is nothing to unlock elsewhere
            IsExtendedGammaRangeUnlocked = true;
            IsAutoStartEnabled = OperatingSystem.IsLinux() && _xdgAutoStartSwitch?.IsSet == true;
        }

        // Trigger UI updates
        OnPropertyChanged(string.Empty);

        return wasLoaded;
    }
}

public partial class SettingsService
{
    [JsonSerializable(typeof(SettingsService))]
    private partial class SerializerContext : JsonSerializerContext;
}
