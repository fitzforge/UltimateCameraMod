using System.Text;
using System.Text.RegularExpressions;
using UltimateCameraMod.Paz;

namespace UltimateCameraMod.Services;

/// <summary>
/// HUD centering for ultrawide monitors. Updates UI HTML/CSS in archive 0012.
/// </summary>
public static class HudMod
{
    private const string UiArchive = "0012";
    private static readonly string[] HudHtmlPaths = { "ui/minimaphudview2.html", "ui/statusgaugeview2.html" };
    private const string CssPath = "ui/gamecommon.css";

    // Since 2.04 the game loads compiled ui/bin__/*.uibinary views instead of the HTML above, so the
    // safe frame can no longer be injected. The view stylesheets are still read as text, so the HUD is
    // moved into a centred band directly in them; world-space trackers are left untouched.
    // Positions are percentages of the screen so they do not depend on the UI's pixel scale.

    // Left-anchored containers: left edge moves to the band's left edge, keeping the original offset.
    private static readonly (string Path, string[] Selectors)[] ShiftLeftCss =
    {
        ("ui/minimaphudview2.css", new[] { ".minimap-hud", "#QuestHudWrap" }),
        ("ui/statusgaugeview2.css", new[] { ".ui-scale-position-left-bot" }),
        // Quest progress notices: their left-mid scale wrap has no id, so the content is moved instead.
        ("ui/alertsystemview.css", new[] { "#ChallangeHudWrap" }),
    };

    // HUD scale wraps narrowed to the band. Wraps with ui-scale-wrap-size-max are already full-screen
    // frames; the others (FullFrame) also get made into one so their absolutely placed children follow.
    private static readonly (string Path, string[] Ids, bool FullFrame)[] BandWrapCss =
    {
        ("ui/alertsystemview.css", AlertWrapIds(), false),
        ("ui/alertsystemoverview.css", AlertWrapIds(), false),
        ("ui/hudview2.css", new[] { "UIScaleHudAdditionalKeyguideList", "UIHudScaleHudKeyGuidWrap" }, false),
        ("ui/hudview2.css", new[] { "UIHudScalePlayGuideHudWrap" }, true),
    };

    // Centre wraps are skipped: centred content stays put, and the small overview slot has no room.
    private static string[] AlertWrapIds() => new[]
    {
        "UIScaleLeftTopWrap", "UIScaleLeftMidWrap", "UIScaleLeftBotWrap",
        "UIScaleRightTopWrap", "UIScaleRightMidWrap", "UIScaleRightBotWrap",
        "UIScaleRightMidKnowledgeDescWrap",
    };

    private const double UiReferenceHeight = 1080.0;

    private const string SafeFrameId = "HUDSafeFrame";
    private const int DefaultMaxWidth = 1920;

    // CentreHUD approach: 16:9 uses class "ui-view-max-size-16-9", 21:9 uses "ui-view-max-size-21-9"
    private static string GetSafeFrameClass(int maxWidth) => maxWidth > 1920 ? "ui-view-max-size-21-9" : "ui-view-max-size-16-9";
    private static string GetSafeFrameOpen(int maxWidth) => $"    <div id=\"{SafeFrameId}\" class=\"{GetSafeFrameClass(maxWidth)}\">";
    private const string SafeFrameClose = "    </div>";

    private static readonly Regex BodyOpenRe = new(@"(<body\b[^>]*>)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BodyCloseRe = new(@"(</body>)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlCommentRe = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private const string GaugeTracker = "<div id=\"HPGaugeTracker\"";
    private const string GaugeSkillPoint = "<div id=\"UIHudScaleSkillPointStatusGaugeContainer\"";

    public static Func<string>? BackupsDirOverride { get; set; }
    private static string BackupsDir => BackupsDirOverride?.Invoke()
        ?? Path.Combine(AppContext.BaseDirectory, "backups", "hud");

    // ── Text compaction ──────────────────────────────────────────────

    private static string CompactCss(string text)
    {
        var t = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        t = Regex.Replace(t, @"\n\s*\n+", "\n");
        t = Regex.Replace(t, @"[ \t]+\n", "\n");
        return Regex.Replace(t, @"\s*([{}:;,])\s*", "$1");
    }

    private static string CompactText(string entryPath, string text)
    {
        if (entryPath.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            return CompactCss(text);
        var t = text;
        if (entryPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            t = HtmlCommentRe.Replace(t, "");
        return Regex.Replace(t, @"\n\s*\n+", "\n");
    }

    // Throws on invalid bytes so encrypted data is not mistaken for plain text.
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    // ── PAZ I/O ──────────────────────────────────────────────────────

    private static List<PazEntry> FindUiEntries(string gameDir)
    {
        string pamt = Path.Combine(gameDir, UiArchive, "0.pamt");
        string pazDir = Path.Combine(gameDir, UiArchive);
        if (!File.Exists(pamt))
            throw new FileNotFoundException($"UI archive not found: {pamt}");

        var entries = PamtReader.Parse(pamt, pazDir);
        var byPath = new Dictionary<string, PazEntry>();
        foreach (var e in entries)
        {
            string key = e.Path.ToLowerInvariant();
            if (!byPath.ContainsKey(key))
                byPath[key] = e;
        }

        var allPaths = HudHtmlPaths.Append(CssPath)
            .Concat(ShiftLeftCss.Select(c => c.Path))
            .Concat(BandWrapCss.Select(c => c.Path))
            .Distinct();
        var result = new List<PazEntry>();
        foreach (var p in allPaths)
        {
            if (!byPath.TryGetValue(p, out var entry))
                throw new FileNotFoundException($"{p} not found in archive");
            result.Add(entry);
        }
        return result;
    }

    private static byte[] ReadRaw(PazEntry entry)
    {
        using var fs = new FileStream(entry.PazFile, FileMode.Open, FileAccess.Read);
        fs.Seek(entry.Offset, SeekOrigin.Begin);
        byte[] data = new byte[entry.CompSize];
        int totalRead = 0;
        while (totalRead < data.Length)
        {
            int n = fs.Read(data, totalRead, data.Length - totalRead);
            if (n == 0) throw new EndOfStreamException();
            totalRead += n;
        }
        return data;
    }

    private static (string Text, bool Encrypted) DecodeText(PazEntry entry, byte[] raw)
    {
        if (entry.Compressed)
        {
            if (entry.CompressionType != 2)
                throw new InvalidOperationException($"Unsupported compression for {entry.Path}");
            try
            {
                byte[] plain = CompressionUtils.Lz4Decompress(raw, entry.OrigSize);
                try { return (StrictUtf8.GetString(plain).TrimEnd('\0'), false); }
                catch (DecoderFallbackException) { }
            }
            catch { }

            try
            {
                byte[] dec = AssetCodec.Decode(raw, Path.GetFileName(entry.Path));
                byte[] dp = CompressionUtils.Lz4Decompress(dec, entry.OrigSize);
                return (Encoding.UTF8.GetString(dp).TrimEnd('\0'), true);
            }
            catch { }

            byte[] fallback = CompressionUtils.Lz4Decompress(raw, entry.OrigSize);
            return (Encoding.UTF8.GetString(fallback).TrimEnd('\0'), false);
        }

        try { return (StrictUtf8.GetString(raw).TrimEnd('\0'), false); }
        catch
        {
            byte[] dec = AssetCodec.Decode(raw, Path.GetFileName(entry.Path));
            return (Encoding.UTF8.GetString(dec).TrimEnd('\0'), true);
        }
    }

    // ── HTML modification ────────────────────────────────────────────

    private static string ModifyHtml(string entryPath, string text, int maxWidth)
    {
        string sfOpen = GetSafeFrameOpen(maxWidth);
        if (text.Contains(SafeFrameId)) return text;
        if (!BodyOpenRe.IsMatch(text)) throw new InvalidOperationException($"No <body> in {entryPath}");
        if (!BodyCloseRe.IsMatch(text)) throw new InvalidOperationException($"No </body> in {entryPath}");

        string modified = BodyOpenRe.Replace(text, $"$1\n{sfOpen}\n", 1);

        if (entryPath.Equals("ui/statusgaugeview2.html", StringComparison.OrdinalIgnoreCase))
        {
            if (!modified.Contains(GaugeTracker)) throw new InvalidOperationException("HPGaugeTracker not found");
            if (!modified.Contains(GaugeSkillPoint)) throw new InvalidOperationException("SkillPointContainer not found");
            modified = modified.Replace(GaugeTracker, $"{SafeFrameClose}\n{GaugeTracker}");
            modified = modified.Replace(GaugeSkillPoint, $"{sfOpen}\n{GaugeSkillPoint}");
        }

        modified = BodyCloseRe.Replace(modified, $"{SafeFrameClose}\n$1", 1);
        return modified;
    }

    // ── CSS modification ─────────────────────────────────────────────

    private static string ModifyCss(string text, int maxWidth = DefaultMaxWidth)
    {
        // CentreHUD approach: update 21-9 to 2520px, then create 16-9 with user's width
        var pat219 = new Regex(@"(\.ui-view-max-size-21-9\s*\{)([^}]*)(\})", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var m219 = pat219.Match(text);
        if (!m219.Success) throw new InvalidOperationException(".ui-view-max-size-21-9 not found in CSS");

        // Step 1: ensure 21-9 has max-width: 2520px
        string body219 = m219.Groups[2].Value;
        var propRe = new Regex(@"(max-width\s*:\s*)([^;]+)(;)", RegexOptions.IgnoreCase);
        string newBody219;
        if (propRe.IsMatch(body219))
            newBody219 = propRe.Replace(body219, "${1}2520px${3}", 1);
        else
            newBody219 = body219.TrimEnd() + " max-width: 2520px;";
        string modified = text[..m219.Groups[2].Index] + newBody219 + text[(m219.Groups[2].Index + m219.Groups[2].Length)..];

        // Step 2: check if 16-9 rule exists
        var pat169 = new Regex(@"(\.ui-view-max-size-16-9\s*\{)([^}]*)(\})", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var m169 = pat169.Match(modified);
        if (m169.Success)
        {
            // Update existing 16-9 rule
            string body169 = m169.Groups[2].Value;
            string newBody169;
            if (propRe.IsMatch(body169))
                newBody169 = propRe.Replace(body169, $"${{1}}{maxWidth}px${{3}}", 1);
            else
                newBody169 = body169.TrimEnd() + $" max-width: {maxWidth}px;";
            return modified[..m169.Groups[2].Index] + newBody169 + modified[(m169.Groups[2].Index + m169.Groups[2].Length)..];
        }

        // Step 3: create 16-9 by cloning 21-9 properties with different max-width
        var m219new = pat219.Match(modified);
        string clonedBody = m219new.Groups[2].Value;
        clonedBody = propRe.Replace(clonedBody, $"${{1}}{maxWidth}px${{3}}", 1);
        string newRule = $".ui-view-max-size-16-9 {{{clonedBody}}}";
        int insertAt = m219new.Index + m219new.Length;
        return modified[..insertAt] + "\n" + newRule + modified[insertAt..];
    }

    /// <summary>Band as percentages of the screen width: (left, width).</summary>
    private static (double Left, double Width) GetBand(int maxWidth, double screenAspect)
    {
        double width = Math.Min(1.0, maxWidth / UiReferenceHeight / screenAspect);
        return ((1.0 - width) / 2 * 100, width * 100);
    }

    private static string Pct(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Moves left-anchored rules to the band's left edge: the original left offset becomes a margin.
    /// Uses left/margin rather than transform because the engine drives transform for HUD scaling.
    /// </summary>
    private static string ShiftLeft(string text, string[] selectors, (double Left, double Width) band)
    {
        foreach (var selector in selectors)
        {
            var rule = new Regex(@"(?<=(?:^|\}|\*/)\s*)" + Regex.Escape(selector) + @"\s*\{([^}]*)\}");
            var m = rule.Match(text);
            if (!m.Success) throw new InvalidOperationException($"{selector} not found in CSS");

            string body = m.Groups[1].Value;
            var left = Regex.Match(body, @"(?<![-\w])left\s*:\s*(-?[\d.]+)px");
            string offset = left.Success ? left.Groups[1].Value + "px" : "0px";
            body = SetCssProp(body, "left", Pct(band.Left));
            body = SetCssProp(body, "margin-left", offset);
            text = text[..m.Groups[1].Index] + body + text[(m.Groups[1].Index + m.Groups[1].Length)..];
        }
        return text;
    }

    /// <summary>Appends id rules that narrow full-screen wraps to the band (ids outrank the shared class).</summary>
    private static string AddBandWraps(string text, string[] ids, bool fullFrame, (double Left, double Width) band)
    {
        var sb = new StringBuilder(text.TrimEnd('\0').TrimEnd());
        string frame = fullFrame ? "position:absolute;top:0px;height:100%;" : "";
        foreach (var id in ids.Where(id => !text.Contains($"#{id}{{")))
            sb.Append($"#{id}{{{frame}left:{Pct(band.Left)};width:{Pct(band.Width)}}}");
        return sb.ToString();
    }

    private static string SetCssProp(string body, string prop, string value)
    {
        var re = new Regex(@"(?<![-\w])" + Regex.Escape(prop) + @"\s*:\s*[^;}]*;?", RegexOptions.IgnoreCase);
        if (re.IsMatch(body))
            return re.Replace(body, $"{prop}: {value};", 1);
        string trimmed = body.TrimEnd();
        if (trimmed.Length > 0 && !trimmed.EndsWith(';')) trimmed += ";";
        return trimmed + $" {prop}: {value}; ";
    }

    // ── Payload building ─────────────────────────────────────────────

    private static byte[] BuildPayload(string modifiedText, PazEntry entry, bool encrypted)
    {
        string compacted = CompactText(entry.Path, modifiedText);
        var candidates = new List<string>();
        if (compacted != modifiedText) candidates.Add(compacted);
        candidates.Add(modifiedText);

        Exception? lastErr = null;
        foreach (var candidate in candidates)
        {
            byte[] plaintext = Encoding.UTF8.GetBytes(candidate);
            try
            {
                byte[] payload;
                if (entry.Compressed)
                {
                    byte[] adjusted = entry.IsHtml
                        ? ArchiveWriter.MatchCompressedSizeHtml(plaintext, entry.CompSize, entry.OrigSize)
                        : entry.IsCss
                            ? ArchiveWriter.MatchCompressedSizeCss(plaintext, entry.CompSize, entry.OrigSize)
                            : ArchiveWriter.MatchCompressedSize(plaintext, entry.CompSize, entry.OrigSize);
                    payload = CompressionUtils.Lz4Compress(adjusted);
                    if (payload.Length != entry.CompSize)
                        throw new InvalidOperationException($"comp_size mismatch: {payload.Length} != {entry.CompSize}");
                }
                else
                {
                    if (plaintext.Length > entry.CompSize)
                        throw new InvalidOperationException($"Too large: {plaintext.Length} > {entry.CompSize}");
                    // Pad with spaces: the slot is read as text up to its full size.
                    payload = Enumerable.Repeat((byte)' ', entry.CompSize).ToArray();
                    Array.Copy(plaintext, payload, plaintext.Length);
                }

                if (encrypted)
                {
                    payload = AssetCodec.Encode(payload, Path.GetFileName(entry.Path));
                    if (payload.Length != entry.CompSize)
                        throw new InvalidOperationException("Encrypted size mismatch");
                }
                return payload;
            }
            catch (Exception e) { lastErr = e; }
        }

        throw new InvalidOperationException($"Cannot fit {entry.Path}: {lastErr?.Message}");
    }

    // ── Backup management ────────────────────────────────────────────

    private static bool BackupExists() => File.Exists(Path.Combine(BackupsDir, "meta.txt"));

    /// <summary>Backs up entries that have no backup yet; existing backups are kept as the vanilla copy.</summary>
    private static void SaveBackups(List<PazEntry> entries)
    {
        Directory.CreateDirectory(BackupsDir);
        foreach (var entry in entries)
        {
            string fname = entry.Path.Replace('/', '_').Replace('\\', '_') + ".bin";
            string path = Path.Combine(BackupsDir, fname);
            if (!File.Exists(path))
                File.WriteAllBytes(path, ReadRaw(entry));
        }
        var meta = string.Join('\n', entries.Select(e => $"{e.Path}|{e.CompSize}|{e.OrigSize}|{e.Offset}"));
        File.WriteAllText(Path.Combine(BackupsDir, "meta.txt"), meta);
    }

    private static byte[]? LoadBackup(PazEntry entry)
    {
        string fname = entry.Path.Replace('/', '_').Replace('\\', '_') + ".bin";
        string path = Path.Combine(BackupsDir, fname);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    // ── Live detection ─────────────────────────────────────────────

    public static bool DetectHudModified(string gameDir)
    {
        try
        {
            var entries = FindUiEntries(gameDir);
            foreach (var entry in entries)
            {
                if (!entry.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    continue;
                byte[] raw = ReadRaw(entry);
                var (text, _) = DecodeText(entry, raw);
                if (text.Contains(SafeFrameId))
                    return true;
            }
        }
        catch { }
        return false;
    }

    // ── Public API ───────────────────────────────────────────────────

    public static Dictionary<string, object> InstallCenteredHud(string gameDir,
        int maxWidth = DefaultMaxWidth, Action<string>? log = null, double screenAspect = 32.0 / 9.0)
    {
        var band = GetBand(maxWidth, screenAspect);
        log?.Invoke($"[HUD] Finding UI entries (max-width: {maxWidth}px, band {Pct(band.Width)} of {screenAspect:0.##}:1 screen)...");
        var entries = FindUiEntries(gameDir);

        var decoded = new Dictionary<string, (string Text, bool Encrypted)>();
        foreach (var entry in entries)
        {
            byte[] raw = ReadRaw(entry);
            decoded[entry.Path] = DecodeText(entry, raw);
        }

        bool alreadyInstalled = entries
            .Where(e => e.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            .Any(e => decoded[e.Path].Text.Contains(SafeFrameId));

        log?.Invoke("[HUD] Saving backups...");
        SaveBackups(entries);

        // If already installed (possibly different mode), restore vanilla first then reinstall
        if (alreadyInstalled)
        {
            log?.Invoke("[HUD] Restoring vanilla before reinstall...");
            foreach (var entry in entries)
            {
                byte[]? backup = LoadBackup(entry);
                if (backup == null || backup.Length != entry.CompSize) continue;
                var restoreTs = ArchiveWriter.SaveTimestamps(entry.PazFile);
                using (var fs = new FileStream(entry.PazFile, FileMode.Open, FileAccess.Write))
                {
                    fs.Seek(entry.Offset, SeekOrigin.Begin);
                    fs.Write(backup);
                }
                restoreTs();
            }
            // Re-decode from restored vanilla
            decoded.Clear();
            foreach (var entry in entries)
            {
                byte[] raw = ReadRaw(entry);
                decoded[entry.Path] = DecodeText(entry, raw);
            }
        }

        var writes = new List<(PazEntry Entry, byte[] Payload)>();
        foreach (var entry in entries)
        {
            var (text, enc) = decoded[entry.Path];
            string modified;
            var shifts = ShiftLeftCss.Where(c => c.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            var wraps = BandWrapCss.Where(c => c.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (entry.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                modified = ModifyHtml(entry.Path, text, maxWidth);
            else if (shifts.Count > 0 || wraps.Count > 0)
            {
                modified = shifts.Aggregate(text, (t, sh) => ShiftLeft(t, sh.Selectors, band));
                modified = wraps.Aggregate(modified, (t, w) => AddBandWraps(t, w.Ids, w.FullFrame, band));
            }
            else if (entry.Path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                modified = ModifyCss(text, maxWidth);
            else continue;

            if (modified == text) continue;
            byte[] payload = BuildPayload(modified, entry, enc);
            writes.Add((entry, payload));
        }

        foreach (var (entry, payload) in writes)
        {
            var restoreTs = ArchiveWriter.SaveTimestamps(entry.PazFile);
            using (var fs = new FileStream(entry.PazFile, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(entry.Offset, SeekOrigin.Begin);
                fs.Write(payload);
            }
            restoreTs();
            log?.Invoke($"[HUD] Updated: {entry.Path}");
        }

        log?.Invoke("[HUD] Done!");
        return new() { ["status"] = "ok" };
    }

    public static Dictionary<string, object> RestoreHud(string gameDir, Action<string>? log = null)
    {
        if (!BackupExists())
            return new() { ["status"] = "no_backup" };

        var entries = FindUiEntries(gameDir);

        // Entries without a backup were never modified (e.g. view CSS added after an older install).
        foreach (var entry in entries)
        {
            byte[]? data = LoadBackup(entry);
            if (data != null && data.Length != entry.CompSize) return new() { ["status"] = "stale_backup" };
        }

        foreach (var entry in entries)
        {
            byte[]? data = LoadBackup(entry);
            if (data == null) continue;
            var restoreTs = ArchiveWriter.SaveTimestamps(entry.PazFile);
            using (var fs = new FileStream(entry.PazFile, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(entry.Offset, SeekOrigin.Begin);
                fs.Write(data);
            }
            restoreTs();
            log?.Invoke($"[HUD] Restored: {entry.Path}");
        }

        return new() { ["status"] = "ok" };
    }
}
