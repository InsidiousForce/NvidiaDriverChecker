using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NvidiaDriverTrayChecker
{
    static class Program
    {
        private static Mutex? mutex;
        private const string MutexName = "Global\\NvidiaDriverTrayChecker_SingleInstance";

        private const string StudioApiUrl =
            "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid=131&pfid=1076&osID=135&languageCode=1033&beta=0&isWHQL=0&dltype=-1&dch=1&upCRD=1&qnf=0&sort1=1&numberOfResults=10";
        private const string GameReadyApiUrl =
            "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid=131&pfid=1076&osID=135&languageCode=1033&beta=0&isWHQL=0&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=1&numberOfResults=10";

        private const string ApiUrl = true ? StudioApiUrl : GameReadyApiUrl;

        private static readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly string DownloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NVIDIA_Drivers");

        [STAThread]
        static void Main()
        {
            mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew) return;

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApplicationContext());
            }
            finally
            {
                mutex?.ReleaseMutex();
                mutex?.Dispose();
            }
        }

        class TrayApplicationContext : ApplicationContext
        {
            private readonly NotifyIcon trayIcon;
            private string? latestVersion, latestUrl, installedVersion;
            private CancellationTokenSource? downloadCts;

            public TrayApplicationContext()
            {
                trayIcon = new NotifyIcon
                {
                    Icon = LoadTrayIcon(false),
                    Visible = true,
                    Text = "NVIDIA Driver Checker",
                    ContextMenuStrip = new ContextMenuStrip()
                };

                trayIcon.DoubleClick += (_, _) => trayIcon.ContextMenuStrip?.Show(Cursor.Position);

                Directory.CreateDirectory(DownloadDir);

                _ = CheckForUpdateAsync();
            }

            private void UiInvoke(Action action)
            {
                if (trayIcon.ContextMenuStrip?.InvokeRequired == true)
                    trayIcon.ContextMenuStrip.Invoke(action);
                else
                    action();
            }

            private void ShowBalloon(string title, string text, ToolTipIcon icon, int timeout = 3000)
            {
                UiInvoke(() =>
                {
                    trayIcon.BalloonTipTitle = title;
                    trayIcon.BalloonTipText = text;
                    trayIcon.BalloonTipIcon = icon;
                    trayIcon.ShowBalloonTip(timeout);
                });
            }

            private void UpdateContextMenu(bool showInstall = false)
            {
                UiInvoke(() =>
                {
                    var menu = trayIcon.ContextMenuStrip!;
                    menu.Items.Clear();

                    if (showInstall)
                        menu.Items.Add($"Update {installedVersion} -> {latestVersion}", null, OnInstallClicked);

                    menu.Items.Add("Check now", null, async (_, _) => await CheckForUpdateAsync());
                    menu.Items.Add("Exit", null, (_, _) => Application.Exit());
                });
            }

            private void SetState(string tipText, ToolTipIcon icon, bool showInstallItem)
            {
                trayIcon.Text = tipText.Length > 63 ? tipText[..60] + "..." : tipText;
                ShowBalloon("NVIDIA Driver Checker", tipText, icon, 6000);
                UpdateContextMenu(showInstallItem);
                SetTrayIcon(showInstallItem);
            }

            private void SetTrayIcon(bool showUpdateDot)
            {
                // generate icon directly on UI thread (small and fast)
                using var icon = LoadTrayIcon(showUpdateDot);
                trayIcon.Icon?.Dispose();
                trayIcon.Icon = (Icon)icon.Clone();
            }

            private Icon LoadTrayIcon(bool showDot)
            {
                string resource = showDot ? "NvidiaDriverTrayChecker.res.tray1.ico" : "NvidiaDriverTrayChecker.res.tray0.ico";
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(resource);
                if (stream == null) throw new Exception($"Resource not found: {resource}");
                return new Icon(stream);
            }

            private async Task CheckForUpdateAsync()
            {
                installedVersion = await Task.Run(() => GetInstalledVersionPowerShell());
                if (string.IsNullOrWhiteSpace(installedVersion))
                {
                    SetState("No NVIDIA driver detected", ToolTipIcon.Warning, false);
                    return;
                }

                (latestVersion, latestUrl) = await GetLatestDriver(ApiUrl);
                if (string.IsNullOrWhiteSpace(latestVersion) || string.IsNullOrWhiteSpace(latestUrl))
                {
                    SetState("Failed to fetch driver info", ToolTipIcon.Error, false);
                    return;
                }

                var updateAvailable = IsNewer(latestVersion, installedVersion);
                SetState(
                    updateAvailable
                        ? $"Update available: {latestVersion} (current: {installedVersion})"
                        : $"Up to date ({installedVersion})",
                    ToolTipIcon.Info,
                    updateAvailable
                );
            }

            private void OnInstallClicked(object? sender, EventArgs e)
            {
                if (sender is not ToolStripItem menuItem || string.IsNullOrWhiteSpace(latestUrl)) return;

                if (downloadCts != null)
                {
                    downloadCts.Cancel();
                    UiInvoke(() => menuItem.Enabled = false);
                    return;
                }

                string filename = Path.GetFileName(latestUrl);
                string targetPath = Path.Combine(DownloadDir, filename);

                _ = DownloadFileAsync(latestUrl, targetPath, menuItem);
            }

            private async Task DownloadFileAsync(string url, string targetPath, ToolStripItem menuItem)
            {
                downloadCts = new CancellationTokenSource();
                UiInvoke(() => menuItem.Text = "Cancel");
                ShowBalloon("Downloading NVIDIA driver", Path.GetFileName(targetPath), ToolTipIcon.Info);

                try
                {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, downloadCts.Token);
                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength ?? -1L;
                    long received = 0;
                    var lastUpdate = DateTime.MinValue;

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    {
                        using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, true))
                        {

                            byte[] buffer = new byte[8192];
                            int read;
                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, downloadCts.Token)) > 0)
                            {
                                await fs.WriteAsync(buffer, 0, read, downloadCts.Token);
                                received += read;

                                if (total > 0 && (DateTime.Now - lastUpdate).TotalSeconds >= 2)
                                {
                                    lastUpdate = DateTime.Now;
                                    int percent = (int)(received * 100 / total);
                                    ShowBalloon("Downloading NVIDIA driver", $"{Path.GetFileName(targetPath)}\n{percent}% downloaded", ToolTipIcon.Info, 1000);
                                    UiInvoke(() => trayIcon.Text = $"Downloading {percent}% ({Path.GetFileName(targetPath)})");
                                }
                            }
                        }
                    }
                    ShowBalloon("Download complete", $"Saved to {targetPath}\nStarting installer...", ToolTipIcon.Info, 5000);
                    UiInvoke(() => trayIcon.Text = $"Download complete. Running installer...");

                    try { File.Delete(targetPath + ":Zone.Identifier"); } catch { }

                    // launch installer safely
                    Process? installer = null;
                    UiInvoke(() => installer = Process.Start(new ProcessStartInfo
                    {
                        FileName = targetPath,
                        WorkingDirectory = Path.GetDirectoryName(targetPath)!,
                        UseShellExecute = true,
                        Verb = "runas"
                    }));

                    if (installer == null) throw new Exception("Failed to start installer");

                    // wait off UI thread
                    await Task.Run(() => installer.WaitForExit());
                    await CheckForUpdateAsync();
                }
                catch (OperationCanceledException)
                {
                    ShowBalloon("Download cancelled", Path.GetFileName(targetPath), ToolTipIcon.Warning);
                }
                catch (Exception ex)
                {
                    ShowBalloon("Download failed", ex.Message, ToolTipIcon.Error, 10000);
                }
                finally
                {
                    downloadCts.Dispose();
                    downloadCts = null;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) trayIcon.Dispose();
                base.Dispose(disposing);
            }
        }

        static string? GetInstalledVersionPowerShell()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments =
                        "-NoProfile -ExecutionPolicy Bypass -Command " +
                        "\"(Get-WmiObject Win32_PnPSignedDriver | " +
                        "Where-Object { $_.DeviceName -like '*nvidia*' " +
                        "-and $_.DeviceName -notlike '*audio*' " +
                        "-and $_.DeviceName -notlike '*USB*' " +
                        "-and $_.DeviceName -notlike '*SHIELD*' })." +
                        "DriverVersion.SubString(6).Remove(1,1).Insert(3,'.')\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                string output = proc?.StandardOutput.ReadToEnd().Trim() ?? "";
                proc?.WaitForExit();

                return string.IsNullOrWhiteSpace(output) ? null : output;
            }
            catch { return null; }
        }

        static async Task<(string ver, string url)> GetLatestDriver(string apiUrl)
        {
            try
            {
                string json = await client.GetStringAsync(apiUrl);
                using JsonDocument doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("IDS", out var ids) && ids.GetArrayLength() > 0)
                {
                    var info = ids[0].GetProperty("downloadInfo");
                    string? version = info.GetProperty("Version").GetString();
                    string? downloadUrl = info.GetProperty("DownloadURL").GetString();

                    if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(downloadUrl))
                        return (version, downloadUrl);
                }
            }
            catch { }
            return ("", "");
        }

        static bool IsNewer(string? latest, string? current)
        {
            if (latest == null || current == null) return false;
            return Version.TryParse(latest, out var lv) &&
                   Version.TryParse(current, out var cv) &&
                   lv > cv;
        }
    }
}
