using CeratoSoundAI.App.ViewModels;

namespace CeratoSoundAI.App.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage(SettingsViewModel vm) { InitializeComponent(); BindingContext = vm; }
    private void OnMenu(object? s, EventArgs e) => Shell.Current.FlyoutIsPresented = true;
}
