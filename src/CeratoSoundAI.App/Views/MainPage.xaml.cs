using System.ComponentModel;
using CeratoSoundAI.App.ViewModels;

namespace CeratoSoundAI.App.Views;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _vm;
    public MainPage(MainViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        vm.State.PropertyChanged += OnState;
    }

    private void OnState(object? s, PropertyChangedEventArgs e) => MainThread.BeginInvokeOnMainThread(Redraw);

    private void Redraw()
    {
        var c = _vm.State.Caps;
        Curve.Update(c.Bands.Select(b => b.CenterHz).ToList(), _vm.State.Current.BandGainsDb, c.MinGainDb == c.MaxGainDb ? -12 : c.MinGainDb, c.MinGainDb == c.MaxGainDb ? 12 : c.MaxGainDb,
            dim: !c.AffectsRealAudio);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();
        Redraw();
    }

    private void OnMenu(object? s, EventArgs e) => Shell.Current.FlyoutIsPresented = true;
}
