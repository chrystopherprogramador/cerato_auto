using CeratoSoundAI.App.ViewModels;

namespace CeratoSoundAI.App.Views;

public partial class ProfilesPage : ContentPage
{
    private readonly ProfilesViewModel _vm;
    public ProfilesPage(ProfilesViewModel vm) { InitializeComponent(); BindingContext = _vm = vm; }
    protected override async void OnAppearing() { base.OnAppearing(); await _vm.LoadAsync(); }
    private void OnMenu(object? s, EventArgs e) => Shell.Current.FlyoutIsPresented = true;
}
