using System.ComponentModel;
using CeratoSoundAI.App.ViewModels;

namespace CeratoSoundAI.App.Views;

public partial class EqualizerPage : ContentPage
{
    private readonly EqualizerViewModel _vm;
    public EqualizerPage(EqualizerViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(EqualizerViewModel.Draft) or nameof(EqualizerViewModel.Selected)) Redraw(); };
        Curve.BandDragged += (_, t) => _vm.SetBand(t.Band, t.Db);
    }

    private void Redraw()
    {
        var c = _vm.State.Caps;
        Curve.Update(c.Bands.Select(b => b.CenterHz).ToList(), _vm.Draft.BandGainsDb, c.MinGainDb, c.MaxGainDb == c.MinGainDb ? c.MinGainDb + 1 : c.MaxGainDb,
            _vm.Selected, interactive: c.BandCount > 0 && c.Level != Domain.ControlLevel.Unavailable || c.Engine == Domain.EngineKind.Manual, dim: !c.AffectsRealAudio,
            compare: _vm.State.Current.BandGainsDb.Count == c.BandCount ? _vm.State.Current.BandGainsDb : null);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.State.RefreshAsync();
        _vm.Load();
        Redraw();
    }

    private void OnMenu(object? s, EventArgs e) => Shell.Current.FlyoutIsPresented = true;
}
