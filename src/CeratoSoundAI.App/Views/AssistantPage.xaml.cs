using CeratoSoundAI.App.ViewModels;

namespace CeratoSoundAI.App.Views;

public partial class AssistantPage : ContentPage
{
    private readonly AssistantViewModel _vm;
    public AssistantPage(AssistantViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        vm.Messages.CollectionChanged += (_, _) =>
        {
            if (vm.Messages.Count > 0) MainThread.BeginInvokeOnMainThread(() => Chat.ScrollTo(vm.Messages.Count - 1, animate: false));
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.State.RefreshAsync();
        if (_vm.Models.Count == 0) await _vm.LoadModelsAsync();
    }

    private void OnMenu(object? s, EventArgs e) => Shell.Current.FlyoutIsPresented = true;
}
