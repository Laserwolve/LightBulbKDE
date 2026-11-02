using Avalonia.Interactivity;
using LightBulb.Framework;
using LightBulb.ViewModels.Components.Settings;

namespace LightBulb.Views.Components.Settings;

public partial class AmbientLightSettingsTabView : UserControl<AmbientLightSettingsTabViewModel>
{
    public AmbientLightSettingsTabView() => InitializeComponent();

    // The view model reads the camera often while the tab is on screen
    private void UserControl_OnLoaded(object? sender, RoutedEventArgs args) =>
        DataContext.IsShown = true;

    private void UserControl_OnUnloaded(object? sender, RoutedEventArgs args) =>
        DataContext.IsShown = false;
}
