using CeratoSoundAI.App.ViewModels;

namespace CeratoSoundAI.App.Views;

public partial class CalibrationPage : ContentPage
{
    private readonly CalibrationViewModel _vm;
    public CalibrationPage(CalibrationViewModel vm) { InitializeComponent(); BindingContext = _vm = vm; }
    protected override void OnAppearing() { base.OnAppearing(); _vm.Load(); }
    private void OnMenu(object? s, EventArgs e) => Shell.Current.FlyoutIsPresented = true;
}
