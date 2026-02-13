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
        private static Mutex? mutex = null;
        private const string MutexName = "Global\\NvidiaDriverTrayChecker_SingleInstance";

        private const string StudioApiUrl =
            "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
            "?func=DriverManualLookup&psid=131&pfid=1076&osID=135&languageCode=1033&beta=0&isWHQL=0" +
            "&dltype=-1&dch=1&upCRD=1&qnf=0&sort1=1&numberOfResults=10";

        private const string GameReadyApiUrl =
            "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
            "?func=DriverManualLookup&psid=131&pfid=1076&osID=135&languageCode=1033&beta=0&isWHQL=0" +
            "&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=1&numberOfResults=10";

        private const string ApiUrl = true ? StudioApiUrl : GameReadyApiUrl;

        private static readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly string DownloadDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NVIDIA_Drivers"
        );

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
            private string? latestVersion;
            private string? latestUrl;
            private string? installedVersion;
            private bool updateAvailable;
            private CancellationTokenSource? downloadCts;

            public TrayApplicationContext()
            {
                trayIcon = new NotifyIcon
                {
                    Icon = GetTrayIcon(false),  // initially no red dot
                    Visible = true,
                    Text = "NVIDIA Driver Checker",
                    ContextMenuStrip = new ContextMenuStrip()
                };

                trayIcon.DoubleClick += (_, _) => trayIcon.ContextMenuStrip.Show(Cursor.Position);

                Directory.CreateDirectory(DownloadDir);

                _ = CheckForUpdateAsync();
            }

            private void UiInvoke(Action action)
            {
                if (trayIcon?.ContextMenuStrip != null && trayIcon.ContextMenuStrip.InvokeRequired)
                    trayIcon.ContextMenuStrip.Invoke(action);
                else
                    action();
            }

            private async Task CheckForUpdateAsync()
            {
                // Run PowerShell version check off the UI thread
                installedVersion = await Task.Run(() => GetInstalledVersionPowerShell());

                if (string.IsNullOrWhiteSpace(installedVersion))
                {
                    SetState("No NVIDIA driver detected", ToolTipIcon.Warning, false);
                    return;
                }

                // Fetch latest driver normally (already async)
                (latestVersion, latestUrl) = await GetLatestDriver(ApiUrl);

                if (string.IsNullOrWhiteSpace(latestVersion) || string.IsNullOrWhiteSpace(latestUrl))
                {
                    SetState("Failed to fetch driver info", ToolTipIcon.Error, false);
                    return;
                }

                updateAvailable = IsNewer(latestVersion, installedVersion);

                SetState(
                    updateAvailable
                        ? $"Update available: {latestVersion} (current: {installedVersion})"
                        : $"Up to date ({installedVersion})",
                    ToolTipIcon.Info,
                    updateAvailable
                );
            }

            private void SetState(string tipText, ToolTipIcon icon, bool showInstallItem)
            {
                UiInvoke(() =>
                {
                    trayIcon.Text = tipText.Length > 63 ? tipText.Substring(0, 60) + "..." : tipText;
                    trayIcon.BalloonTipTitle = "NVIDIA Driver Checker";
                    trayIcon.BalloonTipText = tipText;
                    trayIcon.BalloonTipIcon = icon;

                    trayIcon.ContextMenuStrip?.Items.Clear();

                    if (showInstallItem)
                    {
                        SetTrayIcon(updateAvailable);
                        trayIcon.ContextMenuStrip?.Items.Add(
                            $"Update {installedVersion} -> {latestVersion}",
                            null,
                            OnInstallClicked
                        );
                    }

                    trayIcon.ContextMenuStrip?.Items.Add("Check now", null, async (_, _) => await CheckForUpdateAsync());
                    trayIcon.ContextMenuStrip?.Items.Add("Exit", null, (_, _) => Application.Exit());

                    trayIcon.ShowBalloonTip(6000);
                });
            }

            private void SetTrayIcon(bool showUpdateDot)
            {
                Task.Run(() =>
                {
                    using Icon newIcon = GetTrayIcon(showUpdateDot); // generate off UI thread
                    UiInvoke(() =>
                    {
                        trayIcon.Icon?.Dispose();
                        trayIcon.Icon = (Icon)newIcon.Clone(); // clone to keep handle valid
                    });
                });
            }

            private Icon GetTrayIcon(bool showUpdateDot)
            {
                using Icon baseIcon = showUpdateDot ? LoadEmbeddedIcon("NvidiaDriverTrayChecker.res.tray1.ico") : LoadEmbeddedIcon("NvidiaDriverTrayChecker.res.tray0.ico");

                using var baseBitmap = baseIcon.ToBitmap();
                var bmp = new Bitmap(baseBitmap.Width, baseBitmap.Height);

                using (var g = Graphics.FromImage(bmp))
                {
                    g.DrawImage(baseBitmap, 0, 0, baseBitmap.Width, baseBitmap.Height);

                    if (showUpdateDot && false)
                    {
                        int dotSize = 16;
                        int margin = 2;
                        int x = bmp.Width - dotSize - margin;
                        int y = bmp.Height - dotSize - margin;

                        Color winUpdateOrange = Color.FromArgb(255, 186, 65);
                        using (var brush = new SolidBrush(winUpdateOrange))
                        {
                            g.FillEllipse(brush, x, y, dotSize, dotSize);
                        }

                        using var pen = new Pen(Color.FromArgb(128, 0, 0, 0), 2); // semi-transparent black, thickness = 3
                        g.DrawEllipse(pen, x, y, dotSize, dotSize); // draw stroke around the dot
                    }
                }

                return (Icon) baseIcon.Clone();
                return Icon.FromHandle(bmp.GetHicon());
            }

            private Icon LoadEmbeddedIcon(string resourceName)
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null)
                    throw new Exception($"Resource not found: {resourceName}");
                return new Icon(stream);
            }


            private void OnInstallClicked(object? sender, EventArgs e)
            {
                if (sender is not ToolStripItem menuItem) return;

                if (downloadCts != null)
                {
                    downloadCts.Cancel();
                    UiInvoke(() => menuItem.Enabled = false);
                    return;
                }

                if (string.IsNullOrWhiteSpace(latestUrl)) return;

                string filename = Path.GetFileName(latestUrl);
                string targetPath = Path.Combine(DownloadDir, filename);

                downloadCts = new CancellationTokenSource();
                UiInvoke(() => menuItem.Text = "Cancel");

                UiInvoke(() =>
                {
                    trayIcon.BalloonTipTitle = "Downloading NVIDIA driver";
                    trayIcon.BalloonTipText = filename;
                    trayIcon.BalloonTipIcon = ToolTipIcon.Info;
                    trayIcon.ShowBalloonTip(3000);
                });

                Task.Run(async () =>
                {
                    try
                    {
                        using var response = await client.GetAsync(latestUrl, HttpCompletionOption.ResponseHeadersRead, downloadCts.Token);
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        var receivedBytes = 0L;
                        var lastUpdate = DateTime.MinValue;

                        using var stream = await response.Content.ReadAsStreamAsync();
                        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                        byte[] buffer = new byte[8192];
                        int read;

                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, downloadCts.Token)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, read, downloadCts.Token);
                            receivedBytes += read;

                            if (totalBytes > 0 && (DateTime.Now - lastUpdate).TotalSeconds >= 2)
                            {
                                int percent = (int)(receivedBytes * 100 / totalBytes);
                                lastUpdate = DateTime.Now;

                                UiInvoke(() =>
                                {
                                    trayIcon.BalloonTipText = $"{filename}\n{percent}% downloaded";
                                    trayIcon.ShowBalloonTip(1000);
                                    trayIcon.Text = $"Downloading {percent}% ({filename})";
                                });
                            }
                        }

                        UiInvoke(() =>
                        {
                            trayIcon.BalloonTipTitle = "Download complete";
                            trayIcon.BalloonTipText = $"Saved to {targetPath}\nStarting installer...";
                            trayIcon.ShowBalloonTip(5000);
                        });

                        var installerProcess = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = targetPath,
                                UseShellExecute = true
                            }
                        };
                        installerProcess.Start();

                        UiInvoke(() =>
                        {
                            trayIcon.BalloonTipTitle = "Installer running";
                            trayIcon.BalloonTipText = "Waiting for NVIDIA installer to finish...";
                            trayIcon.ShowBalloonTip(5000);
                        });

                        await Task.Run(() => installerProcess.WaitForExit());

                        await CheckForUpdateAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        UiInvoke(() =>
                        {
                            trayIcon.BalloonTipTitle = "Download cancelled";
                            trayIcon.BalloonTipText = filename;
                            trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
                            trayIcon.ShowBalloonTip(3000);
                        });
                    }
                    catch (Exception ex)
                    {
                        UiInvoke(() =>
                        {
                            trayIcon.BalloonTipTitle = "Download failed";
                            trayIcon.BalloonTipText = ex.Message;
                            trayIcon.BalloonTipIcon = ToolTipIcon.Error;
                            trayIcon.ShowBalloonTip(10000);
                        });
                    }
                    finally
                    {
                        downloadCts.Dispose();
                        downloadCts = null;

                        UiInvoke(() =>
                        {
                            menuItem!.Text = $"Update {installedVersion} -> {latestVersion}";
                            menuItem.Enabled = true;
                            trayIcon.Text = updateAvailable ? $"Update available: {latestVersion}" : $"Up to date ({installedVersion})";
                        });
                    }
                });
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    trayIcon?.Dispose();
                }
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

                if (!string.IsNullOrWhiteSpace(output))
                    return output;
            }
            catch { }

            return null;
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
            return ("", ""); // Non-nullable defaults
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
