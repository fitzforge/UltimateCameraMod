using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UltimateCameraMod.V3.Controls;
using UltimateCameraMod.V3.Localization;
using UltimateCameraMod.V3.Models;
using UltimateCameraMod.Models;
using UltimateCameraMod.Services;

namespace UltimateCameraMod.V3;

public partial class MainWindow : Window
{
    private void OnExportJson(object sender, RoutedEventArgs e)
    {
        var item = _selectedPresetManagerItem;
        string? expName = item?.Name;
        string? expAuthor = item?.SourceLabel;
        string? expDesc = item?.SummaryText;
        string? expUrl = item?.Url;

        // For imported presets, read metadata from the imported preset file
        if (item?.KindId == "imported" && !string.IsNullOrEmpty(item.Name))
        {
            try
            {
                var imported = LoadImportedPreset(item.Name);
                if (imported != null)
                {
                    if (!string.IsNullOrWhiteSpace(imported.Author)) expAuthor = imported.Author;
                    if (!string.IsNullOrWhiteSpace(imported.Description)) expDesc = imported.Description;
                    if (!string.IsNullOrWhiteSpace(imported.Url)) expUrl = imported.Url;
                }
            }
            catch { }
        }
        // For regular presets, try reading from the file
        else if (!string.IsNullOrEmpty(item?.FilePath) && File.Exists(item.FilePath))
        {
            try
            {
                using var reader = new StreamReader(item.FilePath);
                var buf = new char[4096];
                int read = reader.Read(buf, 0, buf.Length);
                string head = new string(buf, 0, read);
                string? fileDesc = ExtractJsonStringField(head, "description");
                string? fileUrl = ExtractJsonStringField(head, "url");
                string? fileAuthor = ExtractJsonStringField(head, "author");
                if (!string.IsNullOrWhiteSpace(fileDesc)) expDesc = fileDesc;
                if (!string.IsNullOrWhiteSpace(fileUrl)) expUrl = fileUrl;
                if (!string.IsNullOrWhiteSpace(fileAuthor)) expAuthor = fileAuthor;
            }
            catch { }
        }

        var ctrl = new ExportJsonDialog(_gameDir, () =>
        {
            // Use the live session XML directly -- it already has all Quick, Fine Tune,
            // and sacred God Mode values applied. Rebuilding from scratch would lose
            // values that CameraRules computed earlier but aren't individually tracked.
            if (!string.IsNullOrWhiteSpace(_sessionXml))
                return _sessionXml;
            CaptureSessionXml();
            return _sessionXml;
        },
        presetName: expName,
        presetAuthor: expAuthor,
        presetDescription: expDesc,
        presetUrl: expUrl,
        isRawImport: _sessionIsRawImport,
        getSettingsPayload: () => BuildCurrentPresetSettingsPayload());
        ctrl.OnCloseRequested = () => CloseOverlay();
        _ = ShowOverlayAsync(ctrl, width: 720, height: 750);
    }

    // ── Install ──────────────────────────────────────────────────

    private void OnInstall(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_gameDir))
        {
            SetStatus(L("Status_GameFolderNotSet"), "Warn");
            return;
        }

        string xml;
        try
        {
            // Always rebuild from current editor state so God Mode edits are captured.
            // BuildSessionXmlForMode routes to BuildGodModeSessionXml (which layers
            // BuildExpertModSet on top) or BuildCuratedSessionXml (which includes
            // ReapplyGodModeOverrides), so all sacred edits are included.
            xml = BuildSessionXmlForMode(_activeMode);
            _sessionXml = xml;
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(L("Status_InstallFailedMessage"), ex.Message), "Error");
            return;
        }

        string gameDir = _gameDir;
        string platform = _detectedPlatform;
        string activeMode = _activeMode;
        string styleId = GetSelectedStyleId();
        bool hudEnabled = CenterHudCheck.IsChecked == true;
        int hudWidth = GetHudWidth();
        double screenAspect = SystemParameters.PrimaryScreenWidth / SystemParameters.PrimaryScreenHeight;

        SetGlobalBusy(true, L("Status_InstallingCamera"));
        SetStatus(L("Status_PreparingSessionXml"), "Accent");
        Task.Run(() =>
        {
            Action<string> log = msg => Dispatcher.Invoke(() => SetStatus(msg, "Accent"));
            static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

            var (beforeEntry, beforeRaw) = CameraMod.ReadCameraEntryWithRawBytes(gameDir);
            Dictionary<string, object> installResult = CameraMod.InstallRawXml(gameDir, xml, log: log);

            // HUD centering (archive 0012, independent from camera)
            if (hudEnabled)
            {
                try
                {
                    log(L("Status_InstallingHud"));
                    HudMod.InstallCenteredHud(gameDir, hudWidth, log, screenAspect);
                }
                catch (Exception hudEx)
                {
                    log(string.Format(L("Status_HudInstallFailed"), hudEx.Message));
                }
            }
            else if (HudMod.DetectHudModified(gameDir))
            {
                try
                {
                    log(L("Status_RestoringHud"));
                    HudMod.RestoreHud(gameDir, log);
                }
                catch (Exception hudEx)
                {
                    log(string.Format(L("Status_HudRestoreFailed"), hudEx.Message));
                }
            }

            var (afterEntry, afterRaw) = CameraMod.ReadCameraEntryWithRawBytes(gameDir);
            bool payloadChanged = !beforeRaw.AsSpan().SequenceEqual(afterRaw);
            long finalPayloadBytes = afterRaw.Length;
            long finalCompBytes = afterEntry.CompSize;
            string tracePath = Path.Combine(ExeDir, "install_trace.txt");
            File.WriteAllText(tracePath,
                $"time_utc={DateTime.UtcNow:O}\n" +
                $"game_dir={gameDir}\n" +
                $"mode={activeMode}\n" +
                $"style_id={styleId}\n" +
                $"session_xml_sha256={HashBytes(Encoding.UTF8.GetBytes(xml))}\n" +
                $"entry_path={afterEntry.Path}\n" +
                $"paz_file={afterEntry.PazFile}\n" +
                $"offset={afterEntry.Offset}\n" +
                $"comp_size={afterEntry.CompSize}\n" +
                $"before_sha256={HashBytes(beforeRaw)}\n" +
                $"after_sha256={HashBytes(afterRaw)}\n" +
                $"payload_changed={(payloadChanged ? "true" : "false")}\n");

            return (installResult, payloadChanged, tracePath, finalPayloadBytes, finalCompBytes);
        })
            .ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    SetGlobalBusy(false);
                    if (t.IsFaulted)
                    {
                        string message = t.Exception?.GetBaseException().Message ?? "Unknown error";
                        SetStatus(string.Format(L("Status_InstallFailedMessage"), message), "Error");
                        return;
                    }

                    QueueSavedToast(L("Label_Installed"));
                    var (installResult, payloadChanged, tracePath, finalPayloadBytes, finalCompBytes) = t.Result;
                    bool ok = installResult.TryGetValue("status", out var statusObj)
                        && string.Equals(statusObj?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
                    if (!ok)
                    {
                        SetStatus(L("Msg_InstallCompletedUnexpected"), "Warn");
                        return;
                    }

                    try
                    {
                        GameInstallBaselineTracker.SaveAfterSuccessfulInstall(ExeDir, Ver, gameDir, platform);
                        _gameUpdateNoticeSessionDismissed = false;
                    }
                    catch { }

                    RefreshGameUpdateNotice();

                    SetStatus(
                        payloadChanged
                            ? string.Format(L("Status_InstalledSession"), $"{finalPayloadBytes:N0}", $"{finalCompBytes:N0}")
                            : string.Format(L("Status_InstalledUnchanged"), $"{finalPayloadBytes:N0}", $"{finalCompBytes:N0}"),
                        payloadChanged ? "Success" : "Warn");
                });
            });
    }

    // ── Restore ──────────────────────────────────────────────────

    private void OnRestore(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_gameDir))
        {
            SetStatus(L("Status_GameFolderNotSet"), "Warn");
            return;
        }

        string gameDir = _gameDir;
        SetGlobalBusy(true, L("Status_RestoringVanillaCamera"));
        SetStatus(L("Status_RestoringVanillaCamera"), "Accent");
        Task.Run(() => CameraMod.RestoreCamera(gameDir,
            log: msg => Dispatcher.Invoke(() => SetStatus(msg, "Accent"))))
            .ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    SetGlobalBusy(false);
                    if (t.IsFaulted)
                    {
                        string message = t.Exception?.GetBaseException().Message ?? "Unknown error";
                        SetStatus(string.Format(L("Status_RestoreFailedMessage"), message), "Error");
                        return;
                    }

                    var result = t.Result;
                    string status = result.TryGetValue("status", out var value) ? value?.ToString() ?? "" : "";
                    switch (status)
                    {
                        case "ok":
                            QueueSavedToast(L("Label_Restored"));
                            SetStatus(L("Status_RestoredVanilla"), "Success");
                            break;
                        case "no_backup":
                            SetStatus(L("Status_NoBackupMayBeVanilla"), "Warn");
                            break;
                        case "stale_backup":
                            SetStatus(L("Status_StaleBackupRefreshed"), "Warn");
                            RefreshGameUpdateNotice();
                            break;
                        default:
                            SetStatus(L("Msg_RestoreCompletedUnconfirmed"), "Warn");
                            break;
                    }
                });
            });
    }

    private void ShowBannerFromState(Dictionary<string, object> state)
    {
        int fov = GetInt(state, "fov", 0);
        string style = state.GetValueOrDefault("style")?.ToString() ?? "default";
        bool bane = GetBool(state, "bane");
        double combatPb = 0.0;
        if (state.TryGetValue("combat_pullback", out var cpbObj) && cpbObj != null)
            double.TryParse(cpbObj.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out combatPb);
        bool mountH = GetBool(state, "mount_height");
        bool steadycam = GetBool(state, "steadycam", true);
        double dist = 5.0, height = 0.0, hshift = 0.0;
        if (state.ContainsKey("custom"))
        {
            try
            {
                var custom = JsonSerializer.Deserialize<JsonElement>(state["custom"].ToString()!);
                dist = custom.TryGetProperty("distance", out var d) ? d.GetDouble() : 5.0;
                height = custom.TryGetProperty("height", out var h) ? h.GetDouble() : 0.0;
                hshift = custom.TryGetProperty("right_offset", out var r) ? r.GetDouble() : 0.0;
            }
            catch { }
        }

        string styleName = style;
        foreach (var (id, lbl) in Styles)
            if (id == style) { styleName = lbl.Split("  -  ")[0]; break; }
        if (style == "custom") styleName = "Custom";

        var parts = new List<string>();

        if (fov != 0)
            parts.Add($"FoV +{fov}\u00b0");

        if (style == "custom")
            parts.Add($"Dist {dist:F1}  |  Height {height:F1}  |  Shift {hshift:F1}");
        else
            parts.Add(styleName);

        if (bane) parts.Add("Centered");

        var globals = new List<string>();
        if (combatPb != 0) globals.Add($"Lock-on {(int)Math.Round(combatPb * 100):+0;-0}%");
        if (mountH) globals.Add("Mount cam");
        if (steadycam) globals.Add("Steadycam");
        if (globals.Count > 0)
            parts.Add(string.Join(", ", globals));

        if (parts.Count == 0)
        {
            BannerPanel.Visibility = Visibility.Collapsed;
            return;
        }

        BannerPanel.Visibility = Visibility.Visible;
        BannerPanel.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x1F, 0x00));
        BannerText.Text = $"\u26A0  Game files modified:  {string.Join("  |  ", parts)}";
        BannerText.Foreground = FindResource("WarnBrush") as Brush;
    }

    // ── Update detection ─────────────────────────────────────────

    private void CheckForUpdate()
    {
        try
        {
            if (_savedState == null)
            {
                BannerPanel.Visibility = Visibility.Collapsed;
                return;
            }
            ShowBannerFromState(_savedState);
        }
        catch { BannerPanel.Visibility = Visibility.Collapsed; }
    }

}
