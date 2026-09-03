using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Lumi.Views.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class SettingsMobileNavigationTests
{
    [Fact]
    public async Task MobileSidebarItemMatchesTheMobileSettingsPage()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    IsOnboarded = true,
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            var viewModel = new MainViewModel(
                new DataStore(data),
                TestCopilot.Shared,
                new UpdateService(),
                startBackgroundJobs: false);
            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = 1100,
                Height = 820
            };

            window.Show();
            try
            {
                viewModel.SelectedNavIndex = 7;
                await PumpAsync();

                var settingsSidebar = window.FindControl<Panel>("SidebarSettings")
                    ?? throw new InvalidOperationException("Settings sidebar was not found.");
                var sidebarList = settingsSidebar.GetVisualDescendants()
                    .OfType<ListBox>()
                    .Single();
                var labels = sidebarList.GetVisualDescendants()
                    .OfType<ListBoxItem>()
                    .Select(item => item.GetVisualDescendants()
                        .OfType<TextBlock>()
                        .First()
                        .Text)
                    .ToArray();

                Assert.Equal(viewModel.SettingsVM.Pages, labels);

                viewModel.SettingsVM.SelectedPageIndex = 2;
                await PumpAsync();

                var settingsView = window.GetVisualDescendants()
                    .OfType<SettingsView>()
                    .Single();
                Assert.True(settingsView.FindControl<Control>("PageMobile")?.IsVisible);
                Assert.False(settingsView.FindControl<Control>("PageGeneral")?.IsVisible);
                Assert.False(settingsView.FindControl<Control>("PageAppearance")?.IsVisible);
                Assert.NotNull(settingsView.FindControl<Button>("MobileWebSetupButton"));
                Assert.NotNull(settingsView.FindControl<Button>("MobileAndroidSetupButton"));
                Assert.NotNull(settingsView.FindControl<Button>("MobileUseTailscaleButton"));
                Assert.NotNull(settingsView.FindControl<Button>("MobileUseLocalNetworkButton"));
                Assert.Null(settingsView.FindControl<ToggleSwitch>("RemoteInsecureLanToggle"));
                Assert.NotNull(settingsView.FindControl<QrCodeControl>("MobileSetupQrCode"));
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
            }
        }, CancellationToken.None);
    }

    private static async Task PumpAsync()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();
    }
}
