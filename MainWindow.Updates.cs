using System.Diagnostics;
using System.Windows.Interop;

namespace SharpAgent;

public partial class MainWindow
{
    CancellationTokenSource? _updateCheck;
    AppUpdates.Release? _readyUpdate;
    bool _updateOffered, _validatingUpdate, _installingUpdate;

    async Task CheckAppUpdateAsync()
    {
        if (!AppPaths.IsInstalled || !File.Exists(AppPaths.InstalledModePath) || !Version.TryParse(App.Version, out var installed)) return;
        using var check = _updateCheck = new();
        try
        {
            _readyUpdate = await Task.Run(async () =>
            {
                using var http = AppNetwork.Client(TimeSpan.FromSeconds(15), maxBytes: 2 * 1024 * 1024, cookies: false,
                    headers: headers => headers.UserAgent.ParseAdd("SharpAgent/" + installed));
                return await new AppUpdates(AppPaths.UpdatesFolder, installed).PrepareAsync(http, check.Token).ConfigureAwait(false);
            }, check.Token);
            ArmAppUpdateOffer();
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            App.LogError(ex);
        }
        finally { _updateCheck = null; }
    }

    void ArmAppUpdateOffer()
    {
        if (_readyUpdate == null || _updateOffered) return;
        ComponentDispatcher.ThreadIdle -= OfferAppUpdate;
        ComponentDispatcher.ThreadIdle += OfferAppUpdate;
    }

    bool UpdateInstallBusy => AnyRunning || ChatFileBusy || _talk != null || _talkClosing || VoiceShown || _voiceInstall != null;

    void OfferAppUpdate(object? sender, EventArgs e)
    {
        var owner = ActiveChatWindow;
        if (_readyUpdate is not { } update || _updateOffered || _validatingUpdate || _installingUpdate ||
            UpdateInstallBusy || !owner.IsVisible || !owner.IsActive || owner.WindowState == WindowState.Minimized) return;
        ComponentDispatcher.ThreadIdle -= OfferAppUpdate;
        _updateOffered = true;
        if (DarkDialog.Ask(owner, Tr("Обновление скачано", "Update downloaded"),
            Tr($"Sharp Agent {update.Version} готов к установке. Приложение закроется и откроется после обновления.",
                $"Sharp Agent {update.Version} is ready to install. The app will close and reopen after the update."),
            Tr("Установить", "Install"), Tr("Позже", "Later")))
            _ = InstallAppUpdateAsync(update);
    }

    async Task InstallAppUpdateAsync(AppUpdates.Release update)
    {
        _validatingUpdate = true;
        try
        {
            var updates = new AppUpdates(AppPaths.UpdatesFolder, Version.Parse(App.Version));
            using var setup = await Task.Run(() => updates.OpenVerifiedAsync(update, default));
            if (setup == null) throw new IOException(Tr("Не удалось проверить скачанный установщик.", "Could not verify the downloaded installer."));
            if (UpdateInstallBusy)
            {
                _updateOffered = false;
                ArmAppUpdateOffer();
                return;
            }
            SaveState();
            var start = new ProcessStartInfo(setup.Name)
            {
                UseShellExecute = false,
                WorkingDirectory = AppPaths.UpdatesFolder,
                ArgumentList = { "/SP-", "/SILENT", "/NORESTART", "/DIR=" + Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), "/SHARPAGENTUPDATE=1" },
            };
            using var process = Process.Start(start) ?? throw new IOException(Tr("Установщик не запустился.", "The installer did not start."));
            _installingUpdate = true;
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            App.LogError(ex);
            ShowToast(Tr("Не удалось установить обновление: ", "Could not install the update: ") + ex.Message, ToastKind.Error);
        }
        finally { _validatingUpdate = false; }
    }

    void StopAppUpdates()
    {
        _updateCheck?.Cancel();
        ComponentDispatcher.ThreadIdle -= OfferAppUpdate;
    }
}
