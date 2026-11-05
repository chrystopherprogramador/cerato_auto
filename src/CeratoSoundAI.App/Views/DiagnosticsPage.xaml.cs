using CeratoSoundAI.App.ViewModels;

namespace CeratoSoundAI.App.Views;

public partial class DiagnosticsPage : ContentPage
{
    public DiagnosticsPage(DiagnosticsViewModel vm) { InitializeComponent(); BindingContext = vm; }
    private void OnMenu(object? s, EventArgs e) => Shell.Current.FlyoutIsPresented = true;
}
