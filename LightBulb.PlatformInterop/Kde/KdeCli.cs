using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LightBulb.PlatformInterop.Kde;

// The command-line tools KDE Plasma ships for its own configuration. kwriteconfig6 edits a config
// file the way System Settings does, and kscreen-doctor speaks KWin's output management protocol,
// which is the only way for another program to set an output's dimming.
[SupportedOSPlatform("linux")]
internal static partial class KdeCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static bool IsAvailable(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dirPath => File.Exists(Path.Combine(dirPath, fileName)));

    private static string? TryRun(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);

            process.Start();

            // Read both streams at once, so that neither pipe can fill up and stall the tool
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(Timeout))
            {
                process.Kill(true);
                Debug.WriteLine($"'{fileName}' did not finish within {Timeout.TotalSeconds}s.");

                return null;
            }

            if (process.ExitCode != 0)
            {
                Debug.WriteLine(
                    $"'{fileName}' failed with exit code {process.ExitCode}: "
                        + standardError.Result.Trim()
                );

                return null;
            }

            return standardOutput.Result;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Failed to run '{fileName}': {ex.Message}");
            return null;
        }
    }

    // --notify makes KWin reload its configuration at once, as it does after System Settings saves.
    // Without it, the change would only take effect the next time KWin starts.
    public static bool TryWriteKWinConfig(string group, string key, string value) =>
        TryRun(
            "kwriteconfig6",
            ["--notify", "--file", "kwinrc", "--group", group, "--key", key, value]
        )
            is not null;

    public static IReadOnlyList<string>? TryGetEnabledOutputNames()
    {
        var json = TryRun("kscreen-doctor", ["--json"]);
        if (json is null)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);

            return document
                .RootElement.GetProperty("outputs")
                .EnumerateArray()
                .Where(o =>
                    o.TryGetProperty("enabled", out var enabled)
                    && enabled.ValueKind == JsonValueKind.True
                    && o.TryGetProperty("connected", out var connected)
                    && connected.ValueKind == JsonValueKind.True
                )
                .Select(o => o.GetProperty("name").GetString())
                .OfType<string>()
                .ToArray();
        }
        catch (Exception ex)
            when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Debug.WriteLine($"Failed to read outputs from kscreen-doctor: {ex.Message}");
            return null;
        }
    }

    // kscreen-doctor's JSON leaves dimming out, so it comes from the listing, which reads
    //   Output: 1 DP-1 52ec3aa7-...
    //       Brightness control: supported, set to 10% and dimming to 100%
    // with terminal colour codes around the labels even when the output is not a terminal.
    public static IReadOnlyDictionary<string, int>? TryGetOutputDimmings()
    {
        var listing = TryRun("kscreen-doctor", ["--outputs"]);
        if (listing is null)
            return null;

        var dimmings = new Dictionary<string, int>(StringComparer.Ordinal);
        var outputName = default(string);

        foreach (var rawLine in listing.Split('\n'))
        {
            var line = ColorCodeRegex().Replace(rawLine, "").Trim();

            if (OutputHeaderRegex().Match(line) is { Success: true } header)
            {
                outputName = header.Groups[1].Value;
            }
            else if (
                outputName is not null
                && DimmingRegex().Match(line) is { Success: true } dimming
            )
            {
                dimmings[outputName] = int.Parse(
                    dimming.Groups[1].Value,
                    CultureInfo.InvariantCulture
                );
            }
        }

        return dimmings;
    }

    // kscreen-doctor reads anything that is not a whole number as 0, which would dim an output as
    // far as KWin allows, so the value is only ever formatted here, from an integer in range.
    public static bool TrySetOutputDimming(IReadOnlyList<string> outputNames, int percent)
    {
        if (outputNames.Count == 0)
            return true;

        var value = Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture);

        return TryRun(
            "kscreen-doctor",
            outputNames.Select(name => $"output.{name}.dimming.{value}").ToArray()
        )
            is not null;
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex ColorCodeRegex();

    [GeneratedRegex(@"^Output:\s+\d+\s+(\S+)")]
    private static partial Regex OutputHeaderRegex();

    [GeneratedRegex(@"dimming to (\d+)%")]
    private static partial Regex DimmingRegex();
}
