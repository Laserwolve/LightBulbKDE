using System;
using FluentAssertions;
using Xunit;

namespace LightBulb.Core.Tests;

public class AmbientLightSpecs
{
    // What a camera reports for a linear amount of light
    private static double Encode(double linearLuma) =>
        Math.Pow(linearLuma, 1 / AmbientLight.CameraGamma);

    [Fact]
    public void I_observe_one_more_stop_when_the_room_is_twice_as_bright()
    {
        // Arrange
        var exposure = TimeSpan.FromMilliseconds(10);

        // Act
        var darkerStops = AmbientLight.GetStops(Encode(0.2), exposure);
        var brighterStops = AmbientLight.GetStops(Encode(0.4), exposure);

        // Assert
        (brighterStops - darkerStops)
            .Should()
            .BeApproximately(1, 0.001);
    }

    [Fact]
    public void I_observe_the_same_stops_when_a_longer_exposure_catches_the_same_room()
    {
        // Arrange
        var shortExposure = TimeSpan.FromMilliseconds(10);
        var longExposure = TimeSpan.FromMilliseconds(40);

        // Act
        var shortStops = AmbientLight.GetStops(Encode(0.1), shortExposure);
        var longStops = AmbientLight.GetStops(Encode(0.4), longExposure);

        // Assert
        longStops.Should().BeApproximately(shortStops, 0.001);
    }

    [Fact]
    public void I_observe_a_finite_reading_for_a_black_frame()
    {
        // Act
        var stops = AmbientLight.GetStops(0, TimeSpan.FromMilliseconds(200));

        // Assert
        double.IsFinite(stops).Should().BeTrue();
    }

    [Fact]
    public void I_cannot_read_the_light_without_an_exposure()
    {
        // Act & assert
        Assert.Throws<ArgumentOutOfRangeException>(() => AmbientLight.GetStops(0.5, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(-2, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 0.25)]
    [InlineData(4, 0.5)]
    [InlineData(8, 1)]
    [InlineData(10, 1)]
    public void I_observe_the_level_between_a_dark_room_and_a_bright_one(
        double stops,
        double expectedLevel
    )
    {
        // Act
        var level = AmbientLight.GetLevel(stops, 0, 8);

        // Assert
        level.Should().BeApproximately(expectedLevel, 0.001);
    }

    [Fact]
    public void I_observe_only_dark_and_bright_when_both_references_are_the_same()
    {
        // Act & assert
        AmbientLight.GetLevel(4.9, 5, 5).Should().Be(0);
        AmbientLight.GetLevel(5, 5, 5).Should().Be(1);
        AmbientLight.GetLevel(4, 5, 3).Should().Be(1);
        AmbientLight.GetLevel(2, 5, 3).Should().Be(0);
    }

    [Theory]
    [InlineData(0, 0.1)]
    [InlineData(0.05, 0.1)]
    [InlineData(0.3, 0.3)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.9, 0.8)]
    [InlineData(1, 0.8)]
    [InlineData(1.5, 0.8)]
    [InlineData(-0.5, 0.1)]
    public void I_observe_the_room_as_the_brightness_held_between_the_two_settings(
        double level,
        double expectedBrightness
    )
    {
        // Act
        var brightness = AmbientLight.GetBrightness(level, 1, 0.1, 0.8);

        // Assert
        brightness.Should().BeApproximately(expectedBrightness, 0.005);
    }

    [Theory]
    [InlineData(0, 0.1)]
    [InlineData(0.05, 0.1)]
    [InlineData(0.4, 0.5)]
    [InlineData(0.6, 0.75)]
    [InlineData(0.8, 1)]
    [InlineData(0.9, 1)]
    public void I_observe_the_screen_brighter_than_the_room_by_the_scale(
        double level,
        double expectedBrightness
    )
    {
        // Act
        var brightness = AmbientLight.GetBrightness(level, 1.25, 0.1, 1);

        // Assert
        brightness.Should().BeApproximately(expectedBrightness, 0.005);
    }

    [Fact]
    public void I_observe_the_same_room_whatever_the_screen_is_at_once_its_glow_is_known()
    {
        // Arrange: a dim screen read the room at 0 stops, a brighter one read it half a stop up
        var dimStops = 0.0;
        var brightStops = Math.Log2(1.5);

        // Act
        var glow = AmbientLight.GetGlowPerDimming(dimStops, 0.1, brightStops, 0.5);
        var roomSeenDim = AmbientLight.CompensateForScreen(dimStops, 0.1, glow);
        var roomSeenBright = AmbientLight.CompensateForScreen(brightStops, 0.5, glow);

        // Assert
        glow.Should().BeGreaterThan(0);
        roomSeenBright.Should().BeApproximately(roomSeenDim, 0.001);
        roomSeenDim.Should().BeLessThan(dimStops);
    }

    [Fact]
    public void I_observe_no_glow_when_the_brighter_screen_read_no_brighter()
    {
        // Act & assert
        AmbientLight.GetGlowPerDimming(1, 0.1, 0.8, 0.5).Should().Be(0);
        AmbientLight.GetGlowPerDimming(1, 0.5, 2, 0.5).Should().Be(0);
    }

    [Fact]
    public void I_observe_a_reading_kept_above_a_floor_when_the_glow_estimate_is_too_high()
    {
        // Act
        var compensated = AmbientLight.CompensateForScreen(3, 1, 1000);

        // Assert
        compensated.Should().BeApproximately(0, 0.001);
    }
}
