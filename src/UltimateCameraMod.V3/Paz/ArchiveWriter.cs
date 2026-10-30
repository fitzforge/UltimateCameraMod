using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace UltimateCameraMod.Paz;

/// <summary>
/// PAZ asset writer: adjusts plaintext so it compresses to an exact target size,
/// then encodes and writes back into the archive.
/// </summary>
public static class ArchiveWriter
{
    public static Action SaveTimestamps(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return () => { };

        try
        {
            var ct = File.GetCreationTimeUtc(path);
            var at = File.GetLastAccessTimeUtc(path);
            var mt = File.GetLastWriteTimeUtc(path);
            return () =>
            {
                try
                {
                    File.SetCreationTimeUtc(path, ct);
                    File.SetLastAccessTimeUtc(path, at);
                    File.SetLastWriteTimeUtc(path, mt);
                }
                catch { }
            };
        }
        catch { return () => { }; }
    }

    // ── Size matching ────────────────────────────────────────────────

    private static byte[] PadToOrigSize(byte[] data, int origSize)
    {
        if (data.Length >= origSize)
        {
            byte[] trimmed = new byte[origSize];
            Array.Copy(data, trimmed, origSize);
            return trimmed;
        }
        byte[] padded = new byte[origSize];
        Array.Copy(data, padded, data.Length);
        return padded;
    }

    private static byte[] PadWithRandomComment(byte[] data, int origSize)
    {
        if (data.Length >= origSize)
            return PadToOrigSize(data, origSize);

        int gap = origSize - data.Length;
        // <!-- ... --> = 7 bytes overhead; need at least 8 bytes gap for a useful comment
        if (gap < 8)
            return PadToOrigSize(data, origSize);

        int bodyLen = gap - 7; // 4 for "<!--" + 3 for "-->"
        byte[] body = MakeXmlSafeRandomContent(bodyLen);

        byte[] result = new byte[origSize];
        Array.Copy(data, result, data.Length);

        int pos = data.Length;
        result[pos++] = 0x3C; // <
        result[pos++] = 0x21; // !
        result[pos++] = 0x2D; // -
        result[pos++] = 0x2D; // -
        Array.Copy(body, 0, result, pos, bodyLen);
        pos += bodyLen;
        result[pos++] = 0x2D; // -
        result[pos++] = 0x2D; // -
        result[pos++] = 0x3E; // >

        return result;
    }

    private static List<(int Start, int End)> FindXmlComments(byte[] data)
    {
        var comments = new List<(int, int)>();
        int searchFrom = 0;
        byte[] open = new byte[] { 0x3C, 0x21, 0x2D, 0x2D };
        byte[] close = new byte[] { 0x2D, 0x2D, 0x3E };

        while (true)
        {
            int start = IndexOf(data, open, searchFrom);
            if (start == -1) break;
            int contentStart = start + 4;
            int end = IndexOf(data, close, contentStart);
            if (end == -1) break;
            if (end > contentStart)
                comments.Add((contentStart, end));
            searchFrom = end + 3;
        }
        return comments;
    }

    private static int IndexOf(byte[] data, byte[] pattern, int startFrom)
    {
        for (int i = startFrom; i <= data.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static byte[] ShrinkToOrigSize(byte[] data, int origSize)
    {
        if (data.Length <= origSize)
            return PadToOrigSize(data, origSize);

        int excess = data.Length - origSize;
        var result = new List<byte>(data);

        // Strategy 1: trim comment bodies (recalculate after each removal)
        while (excess > 0)
        {
            var comments = FindXmlComments(result.ToArray());
            comments.Sort((a, b) => (b.End - b.Start).CompareTo(a.End - a.Start));
            bool trimmed = false;
            foreach (var (cstart, cend) in comments)
            {
                int bodyLen = cend - cstart;
                int removable = bodyLen - 1;
                if (removable <= 0) continue;
                int toRemove = Math.Min(removable, excess);
                result.RemoveRange(cstart + 1, toRemove);
                excess -= toRemove;
                trimmed = true;
                break;
            }
            if (!trimmed) break;
        }

        if (excess <= 0)
            return FinalizeToSize(result, origSize);

        // Strategy 2: remove adjacent duplicate whitespace
        for (int i = result.Count - 1; i > 0 && excess > 0; i--)
        {
            if (IsWhitespace(result[i]) && IsWhitespace(result[i - 1]))
            {
                result.RemoveAt(i);
                excess--;
            }
        }

        if (excess <= 0)
            return FinalizeToSize(result, origSize);

        // Strategy 3: remove entire comments including delimiters
        while (excess > 0)
        {
            var comments = FindXmlComments(result.ToArray());
            if (comments.Count == 0) break;
            bool removed = false;
            foreach (var (cstart, cend) in comments)
            {
                int fullStart = cstart - 4;
                int fullEnd = cend + 3;
                int removable = fullEnd - fullStart;
                if (removable <= excess + 7)
                {
                    int toRemove = Math.Min(removable, excess);
                    result.RemoveRange(fullStart, toRemove);
                    excess -= toRemove;
                    removed = true;
                    break;
                }
            }
            if (!removed) break;
        }

        if (result.Count > origSize)
            throw new InvalidOperationException(
                $"Modified file is {data.Length - origSize} bytes over orig_size ({origSize}). " +
                $"Could only trim {data.Length - result.Count} bytes.");

        return FinalizeToSize(result, origSize);
    }

    private static byte[] FinalizeToSize(List<byte> result, int origSize)
    {
        byte[] final_ = new byte[origSize];
        int copyLen = Math.Min(result.Count, origSize);
        for (int i = 0; i < copyLen; i++)
            final_[i] = result[i];
        return final_;
    }

    private static bool IsWhitespace(byte b) => b == 0x20 || b == 0x09;

    private static byte[] MakeXmlSafeRandomContent(int length)
    {
        byte[] alphabet = BuildAlphabet();
        byte[] rand = RandomNumberGenerator.GetBytes(length);
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
            result[i] = alphabet[rand[i] % alphabet.Length];
        return result;
    }

    /// <summary>Random comment body that cannot terminate a CSS comment early (no '*' or '/').</summary>
    private static byte[] MakeCssCommentSafeRandomContent(int length)
    {
        byte[] alphabet = BuildAlphabet().Where(c => c != (byte)'*' && c != (byte)'/').ToArray();
        byte[] rand = RandomNumberGenerator.GetBytes(length);
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
            result[i] = alphabet[rand[i] % alphabet.Length];
        return result;
    }

    private static byte[]? _alphabet;
    private static byte[] BuildAlphabet()
    {
        if (_alphabet != null) return _alphabet;
        var list = new List<byte>();
        for (int c = 0x20; c < 0x7F; c++)
        {
            if (c != 0x2D && c != 0x3C && c != 0x3E && c != 0x26)
                list.Add((byte)c);
        }
        _alphabet = list.ToArray();
        return _alphabet;
    }

    private static List<int> FindInsertionPoints(byte[] data)
    {
        var points = new List<int>();
        for (int i = 0; i < data.Length; i++)
            if (data[i] == 0x0A) points.Add(i);
        return points;
    }

    private static byte[]? InflateWithComments(byte[] padded, int plaintextLen,
        int targetCompSize, int targetOrigSize)
    {
        int paddingAvailable = targetOrigSize - plaintextLen;
        int baseComp = CompressionUtils.Lz4Compress(padded).Length;
        int needed = targetCompSize - baseComp;
        if (needed <= 0) return null;

        if (paddingAvailable > 0)
        {
            int maxReplaceable = paddingAvailable;

            byte[] BuildZeroTrial(int n)
            {
                byte[] trial = (byte[])padded.Clone();
                for (int i = 0; i < n; i++)
                    trial[plaintextLen + i] = 0x20;
                return trial;
            }

            int cOne = CompressionUtils.Lz4Compress(BuildZeroTrial(1)).Length;
            if (cOne <= targetCompSize)
            {
                int lo = 1, hi = maxReplaceable;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    int c = CompressionUtils.Lz4Compress(BuildZeroTrial(mid)).Length;
                    if (c == targetCompSize) return BuildZeroTrial(mid);
                    else if (c < targetCompSize) lo = mid + 1;
                    else hi = mid - 1;
                }
                for (int n = Math.Max(1, lo - 5); n < Math.Min(lo + 5, maxReplaceable + 1); n++)
                {
                    var trial = BuildZeroTrial(n);
                    if (CompressionUtils.Lz4Compress(trial).Length == targetCompSize)
                        return trial;
                }
            }
        }

        if (paddingAvailable >= 8)
        {
            int maxBody = paddingAvailable - 7;
            byte[] randBody = MakeXmlSafeRandomContent(maxBody);

            byte[] BuildCommentTrial(int bodyLen)
            {
                byte[] body = new byte[bodyLen];
                Array.Copy(randBody, body, bodyLen);
                byte[] comment = ConcatBytes(new byte[] { 0x3C, 0x21, 0x2D, 0x2D }, body, new byte[] { 0x2D, 0x2D, 0x3E });
                byte[] prefix = new byte[plaintextLen];
                Array.Copy(padded, prefix, plaintextLen);
                byte[] combined = ConcatBytes(prefix, comment);

                byte[] trial = new byte[targetOrigSize];
                int copyLen = Math.Min(combined.Length, targetOrigSize);
                Array.Copy(combined, trial, copyLen);
                return trial;
            }

            int cMin = CompressionUtils.Lz4Compress(BuildCommentTrial(0)).Length;
            int cMax = CompressionUtils.Lz4Compress(BuildCommentTrial(maxBody)).Length;
            if (cMin <= targetCompSize && targetCompSize <= cMax)
            {
                int lo = 0, hi = maxBody;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    var trial = BuildCommentTrial(mid);
                    int c = CompressionUtils.Lz4Compress(trial).Length;
                    if (c == targetCompSize) return trial;
                    else if (c < targetCompSize) lo = mid + 1;
                    else hi = mid - 1;
                }
                for (int n = Math.Max(0, lo - 20); n < Math.Min(lo + 20, maxBody + 1); n++)
                {
                    var trial = BuildCommentTrial(n);
                    if (CompressionUtils.Lz4Compress(trial).Length == targetCompSize)
                        return trial;
                }
            }
        }

        byte[] plaintext = new byte[plaintextLen];
        Array.Copy(padded, plaintext, plaintextLen);

        int tailWs = 0;
        for (int i = plaintext.Length - 1; i >= 0; i--)
        {
            if (plaintext[i] == 0x20 || plaintext[i] == 0x09 ||
                plaintext[i] == 0x0D || plaintext[i] == 0x0A)
                tailWs++;
            else break;
        }
        int effectiveBudget = paddingAvailable + tailWs;
        byte[] baseContent = new byte[plaintextLen - tailWs];
        Array.Copy(plaintext, baseContent, baseContent.Length);

        var newlines = FindInsertionPoints(plaintext);
        if (newlines.Count > 0 && effectiveBudget >= 7)
        {
            foreach (int nSlotsTry in new[] { 50, 100, 200, Math.Min(500, newlines.Count) })
            {
                int actualSlots = Math.Min(nSlotsTry, newlines.Count);
                int step = Math.Max(1, newlines.Count / actualSlots);
                var slots = new List<int>();
                for (int i = 0; i < newlines.Count && slots.Count < actualSlots; i += step)
                    slots.Add(newlines[i]);

                int maxSlotsUsable = Math.Min(slots.Count, effectiveBudget / 7);
                if (maxSlotsUsable < 1) continue;
                int maxTotalBody = effectiveBudget - maxSlotsUsable * 7;
                if (maxTotalBody <= 0) continue;

                for (int attempt = 0; attempt < 8; attempt++)
                {
                    byte[] randPool = MakeXmlSafeRandomContent(
                        maxTotalBody + maxSlotsUsable * 7 + 8);
                    var capturedSlots = slots;
                    var capturedMsu = maxSlotsUsable;
                    var capturedBc = baseContent;
                    var capturedRp = randPool;

                    byte[] BuildMultiTrial(int totalBody)
                    {
                        int nActive = capturedMsu;
                        int perSlot = totalBody / nActive;
                        int remainder = totalBody % nActive;
                        var insertions = new List<(int Pos, byte[] Comment)>();
                        int poolOffset = 0;
                        for (int si = 0; si < nActive; si++)
                        {
                            int bodyLen = perSlot + (si < remainder ? 1 : 0);
                            byte[] body = new byte[bodyLen];
                            Array.Copy(capturedRp, poolOffset, body, 0, bodyLen);
                            poolOffset += bodyLen;
                            byte[] comment = ConcatBytes(new byte[] { 0x3C, 0x21, 0x2D, 0x2D }, body, new byte[] { 0x2D, 0x2D, 0x3E });
                            insertions.Add((capturedSlots[si], comment));
                        }
                        var result = new List<byte>(capturedBc);
                        insertions.Sort((a, b) => b.Pos.CompareTo(a.Pos));
                        foreach (var (pos, comment) in insertions)
                        {
                            int insertAt = Math.Min(pos + 1, result.Count);
                            result.InsertRange(insertAt, comment);
                        }
                        byte[] final_ = new byte[targetOrigSize];
                        int copyLen = Math.Min(result.Count, targetOrigSize);
                        for (int i = 0; i < copyLen; i++)
                            final_[i] = result[i];
                        return final_;
                    }

                    int cMinM = CompressionUtils.Lz4Compress(BuildMultiTrial(0)).Length;
                    int cMaxM = CompressionUtils.Lz4Compress(BuildMultiTrial(maxTotalBody)).Length;
                    if (!(cMinM <= targetCompSize && targetCompSize <= cMaxM))
                        continue;

                    int lo = 0, hi = maxTotalBody;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) / 2;
                        var trial = BuildMultiTrial(mid);
                        int c = CompressionUtils.Lz4Compress(trial).Length;
                        if (c == targetCompSize) return trial;
                        else if (c < targetCompSize) lo = mid + 1;
                        else hi = mid - 1;
                    }
                    for (int n = Math.Max(0, lo - 30); n < Math.Min(lo + 30, maxTotalBody + 1); n++)
                    {
                        var trial = BuildMultiTrial(n);
                        if (CompressionUtils.Lz4Compress(trial).Length == targetCompSize)
                            return trial;
                    }
                }
            }
        }

        return null;
    }

    private static byte[]? InflateByReplacingCommentBodies(byte[] padded, int targetCompSize)
    {
        var comments = FindXmlComments(padded);
        if (comments.Count == 0) return null;

        var positions = new List<int>();
        foreach (var (cstart, cend) in comments)
            for (int i = cstart; i < cend; i++)
                positions.Add(i);
        if (positions.Count == 0) return null;

        int total = positions.Count;

        byte[]? TryFill(byte[] randFill)
        {
            byte[] BuildTrial(int n)
            {
                byte[] trial = (byte[])padded.Clone();
                for (int idx = 0; idx < n; idx++)
                    trial[positions[idx]] = randFill[idx];
                return trial;
            }

            int cNone = CompressionUtils.Lz4Compress(BuildTrial(0)).Length;
            int cAll = CompressionUtils.Lz4Compress(BuildTrial(total)).Length;
            if (targetCompSize < cNone || targetCompSize > cAll) return null;

            int lo = 0, hi = total;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int c = CompressionUtils.Lz4Compress(BuildTrial(mid)).Length;
                if (c == targetCompSize) return BuildTrial(mid);
                else if (c < targetCompSize) lo = mid + 1;
                else hi = mid - 1;
            }

            for (int n = Math.Max(0, lo - 50); n < Math.Min(lo + 50, total + 1); n++)
                if (CompressionUtils.Lz4Compress(BuildTrial(n)).Length == targetCompSize)
                    return BuildTrial(n);

            return null;
        }

        for (int i = 0; i < 8; i++)
        {
            var result = TryFill(MakeXmlSafeRandomContent(total));
            if (result != null) return result;
        }
        return null;
    }

    private static byte[]? InflateByReplacingWhitespaceRuns(byte[] padded, int targetCompSize)
    {
        var runs = new List<(int Start, int End)>();
        int i = 0;
        while (i < padded.Length)
        {
            if (padded[i] == 0x20 || padded[i] == 0x09 || padded[i] == 0x0D || padded[i] == 0x0A)
            {
                int runStart = i;
                while (i < padded.Length && (padded[i] == 0x20 || padded[i] == 0x09 ||
                       padded[i] == 0x0D || padded[i] == 0x0A))
                    i++;
                if (i - runStart >= 8)
                    runs.Add((runStart, i));
            }
            else i++;
        }
        if (runs.Count == 0) return null;

        int totalSlots = runs.Count;
        int totalBody = runs.Sum(r => Math.Max(0, (r.End - r.Start) - 7));

        byte[]? TryFill(byte[] randFill)
        {
            byte[] BuildTrial(int nActive)
            {
                byte[] trial = (byte[])padded.Clone();
                int fillOffset = 0;
                for (int idx = 0; idx < nActive; idx++)
                {
                    var (start, end) = runs[idx];
                    int runLen = end - start;
                    int bodyLen = runLen - 7;
                    byte[] body = new byte[bodyLen];
                    Array.Copy(randFill, fillOffset, body, 0, bodyLen);
                    fillOffset += bodyLen;
                    byte[] comment = ConcatBytes(new byte[] { 0x3C, 0x21, 0x2D, 0x2D }, body, new byte[] { 0x2D, 0x2D, 0x3E });
                    Array.Copy(comment, 0, trial, start, comment.Length);
                    for (int j = start + comment.Length; j < end; j++)
                        trial[j] = 0x20;
                }
                return trial;
            }

            int cNone = CompressionUtils.Lz4Compress(BuildTrial(0)).Length;
            int cAll = CompressionUtils.Lz4Compress(BuildTrial(totalSlots)).Length;
            if (targetCompSize < cNone || targetCompSize > cAll) return null;

            int lo = 0, hi = totalSlots;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int c = CompressionUtils.Lz4Compress(BuildTrial(mid)).Length;
                if (c == targetCompSize) return BuildTrial(mid);
                else if (c < targetCompSize) lo = mid + 1;
                else hi = mid - 1;
            }

            for (int n = Math.Max(0, lo - 10); n < Math.Min(lo + 10, totalSlots + 1); n++)
            {
                var trial = BuildTrial(n);
                if (CompressionUtils.Lz4Compress(trial).Length == targetCompSize)
                    return trial;
            }
            return null;
        }

        for (int attempt = 0; attempt < 12; attempt++)
        {
            var result = TryFill(MakeXmlSafeRandomContent(totalBody + 16));
            if (result != null) return result;
        }
        return null;
    }

    private static byte[]? PadWithScatteredComments(byte[] plaintext, int targetCompSize, int targetOrigSize)
    {
        int gap = targetOrigSize - plaintext.Length;
        if (gap < 8) return null;

        // Find all newline positions as potential comment insertion points
        var newlines = new List<int>();
        for (int i = 0; i < plaintext.Length; i++)
            if (plaintext[i] == 0x0A) newlines.Add(i);
        if (newlines.Count < 4) return null;

        // Try different slot counts to find one that works
        foreach (int targetSlots in new[] { 20, 50, 100, 200, 400, Math.Min(800, newlines.Count / 2) })
        {
            int numSlots = Math.Min(targetSlots, newlines.Count);
            if (numSlots < 1) continue;

            int step = newlines.Count / numSlots;
            var slots = new List<int>();
            for (int i = 0; i < newlines.Count && slots.Count < numSlots; i += step)
                slots.Add(newlines[i]);
            if (slots.Count == 0) continue;

            int overhead = slots.Count * 7; // 7 bytes per <!--X-->
            if (overhead >= gap) continue;
            int maxTotalBody = gap - overhead;

            for (int attempt = 0; attempt < 6; attempt++)
            {
                byte[] randPool = MakeXmlSafeRandomContent(maxTotalBody + 64);
                int slotCount = slots.Count;

                byte[] BuildTrial(int totalBody)
                {
                    int perSlot = totalBody / slotCount;
                    int remainder = totalBody % slotCount;

                    // Build insertions in reverse order so positions stay valid
                    var output = new List<byte>(plaintext);
                    int poolOff = 0;
                    for (int si = slotCount - 1; si >= 0; si--)
                    {
                        int bodyLen = perSlot + (si < remainder ? 1 : 0);
                        int insertAt = Math.Min(slots[si] + 1, output.Count);

                        // Build comment: <!--body-->
                        var comment = new List<byte>(bodyLen + 7);
                        comment.Add(0x3C); comment.Add(0x21); comment.Add(0x2D); comment.Add(0x2D);
                        for (int b = 0; b < bodyLen; b++)
                            comment.Add(randPool[(poolOff + b) % randPool.Length]);
                        poolOff += bodyLen;
                        comment.Add(0x2D); comment.Add(0x2D); comment.Add(0x3E);

                        output.InsertRange(insertAt, comment);
                    }

                    byte[] result = new byte[targetOrigSize];
                    int copyLen = Math.Min(output.Count, targetOrigSize);
                    for (int i = 0; i < copyLen; i++)
                        result[i] = output[i];
                    return result;
                }

                // Binary search on totalBody
                int cMin = CompressionUtils.Lz4Compress(BuildTrial(0)).Length;
                int cMax = CompressionUtils.Lz4Compress(BuildTrial(maxTotalBody)).Length;

                if (targetCompSize < cMin || targetCompSize > cMax) continue;

                int lo = 0, hi = maxTotalBody;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    int c = CompressionUtils.Lz4Compress(BuildTrial(mid)).Length;
                    if (c == targetCompSize) return BuildTrial(mid);
                    else if (c < targetCompSize) lo = mid + 1;
                    else hi = mid - 1;
                }

                // Linear scan near the boundary
                for (int n = Math.Max(0, lo - 40); n < Math.Min(lo + 40, maxTotalBody + 1); n++)
                {
                    var trial = BuildTrial(n);
                    if (CompressionUtils.Lz4Compress(trial).Length == targetCompSize)
                        return trial;
                }
            }
        }

        return null;
    }

    private static byte[]? InflateByRandomCommentPadding(byte[] plaintext, int targetCompSize, int targetOrigSize, bool css = false)
    {
        // Binary search on random comment body length to hit the exact compressed size.
        // We append <!--RANDOM_BODY--> then pad remaining bytes with nulls.
        int available = targetOrigSize - plaintext.Length;
        if (available < 8) return null; // not enough room for <!-- + X + -->

        int maxBody = available - 7;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            byte[] randPool = css ? MakeCssCommentSafeRandomContent(maxBody) : MakeXmlSafeRandomContent(maxBody);
            byte[] open = css ? System.Text.Encoding.ASCII.GetBytes("/*!-") : System.Text.Encoding.ASCII.GetBytes("<!--");
            byte[] close = css ? System.Text.Encoding.ASCII.GetBytes("-*/") : System.Text.Encoding.ASCII.GetBytes("-->");

            byte[] BuildTrial(int bodyLen)
            {
                byte[] buf = new byte[targetOrigSize];
                Array.Copy(plaintext, buf, plaintext.Length);
                int pos = plaintext.Length;
                Array.Copy(open, 0, buf, pos, open.Length);
                pos += open.Length;
                Array.Copy(randPool, 0, buf, pos, bodyLen);
                pos += bodyLen;
                Array.Copy(close, 0, buf, pos, close.Length);
                return buf;
            }

            int cMin = CompressionUtils.Lz4Compress(BuildTrial(1)).Length;
            int cMax = CompressionUtils.Lz4Compress(BuildTrial(maxBody)).Length;
            if (targetCompSize < cMin || targetCompSize > cMax) continue;

            int lo = 1, hi = maxBody;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int c = CompressionUtils.Lz4Compress(BuildTrial(mid)).Length;
                if (c == targetCompSize) return BuildTrial(mid);
                else if (c < targetCompSize) lo = mid + 1;
                else hi = mid - 1;
            }

            for (int n = Math.Max(1, lo - 30); n < Math.Min(lo + 30, maxBody + 1); n++)
            {
                var trial = BuildTrial(n);
                if (CompressionUtils.Lz4Compress(trial).Length == targetCompSize)
                    return trial;
            }
        }

        return null;
    }

    public static byte[] MatchCompressedSize(byte[] plaintext, int targetCompSize, int targetOrigSize)
    {
        byte[] padded;
        if (plaintext.Length > targetOrigSize)
        {
            padded = ShrinkToOrigSize(plaintext, targetOrigSize);
        }
        else
        {
            padded = PadToOrigSize(plaintext, targetOrigSize);
        }

        byte[] comp = CompressionUtils.Lz4Compress(padded);
        if (comp.Length == targetCompSize)
            return padded;

        int delta = comp.Length - targetCompSize;

        if (delta < 0)
        {
            // The file compresses too well. Scatter random XML comments throughout
            // the content to break LZ4's pattern matching and increase compressed size.
            var result = PadWithScatteredComments(plaintext, targetCompSize, targetOrigSize);
            if (result != null) return result;

            int effectiveLen = Math.Min(plaintext.Length, targetOrigSize);
            result = InflateWithComments(padded, effectiveLen, targetCompSize, targetOrigSize);
            if (result != null) return result;

            result = InflateByReplacingCommentBodies(padded, targetCompSize);
            if (result != null) return result;

            result = InflateByReplacingWhitespaceRuns(padded, targetCompSize);
            if (result != null) return result;

            throw new InvalidOperationException(
                $"Cannot match target comp_size {targetCompSize} (got {comp.Length}, delta {delta}). " +
                $"File compresses too well.");
        }

        // Compressed payload is larger than the game's camera slot allows. Do not mutate XML bytes
        // (replacing characters with spaces) — that produces invalid XML and crashes the game at load.
        int overBy = comp.Length - targetCompSize;
        throw new InvalidOperationException(
            $"Preset is too large to install — camera data exceeds the game's limit by {overBy:N0} bytes " +
            $"({comp.Length:N0} / {targetCompSize:N0} bytes).\n\n" +
            "This usually means:\n" +
            "• Your vanilla backup was captured from already-modified game files. " +
            "Delete the 'backups' folder next to UltimateCameraMod.exe, verify game files on Steam, then try again.\n" +
            "• Too many Fine Tune / God Mode edits. Try a simpler preset or reduce the number of overrides.\n" +
            "• The game was updated. Verify game files on Steam and reinstall.");
    }

    // ── HTML size matching (with SHRINK support for HUD files) ─────

    /// <summary>
    /// Size-matches HTML content using CentreHUD's shrink strategy.
    /// When compressed > target, replaces non-space bytes with spaces
    /// (spaces are maximally compressible in LZ4) using a linear scan.
    /// </summary>
    public static byte[] MatchCompressedSizeHtml(byte[] plaintext, int targetCompSize, int targetOrigSize)
    {
        byte[] padded = plaintext.Length > targetOrigSize
            ? ShrinkToOrigSize(plaintext, targetOrigSize)
            : PadToOrigSize(plaintext, targetOrigSize);

        byte[] comp = CompressionUtils.Lz4Compress(padded);
        if (comp.Length == targetCompSize)
            return padded;

        if (comp.Length < targetCompSize)
        {
            // INFLATE: match CentreHUD's approach -- single trailing <!--random--> comment.
            // This avoids scattered comments that trigger the Coherent Gameface watermark.
            var result = InflateByRandomCommentPadding(plaintext, targetCompSize, targetOrigSize);
            if (result != null) return result;

            // Fallback: try InflateWithComments strategies (null->space, then distributed)
            result = InflateWithComments(padded, plaintext.Length, targetCompSize, targetOrigSize);
            if (result != null) return result;

            throw new InvalidOperationException(
                $"Cannot inflate HTML to target comp_size {targetCompSize} (got {comp.Length}).");
        }

        // SHRINK: compressed > target. Replace non-space bytes with spaces.
        // CentreHUD uses the same approach: spaces are maximally compressible in LZ4.
        var adjacent = new List<int>();
        var nonAdjacent = new List<int>();
        for (int i = 0; i < padded.Length; i++)
        {
            if (padded[i] == 0x20) continue;
            if ((i > 0 && padded[i - 1] == 0x20) || (i + 1 < padded.Length && padded[i + 1] == 0x20))
                adjacent.Add(i);
            else
                nonAdjacent.Add(i);
        }
        var candidates = new List<int>(adjacent);
        candidates.AddRange(nonAdjacent);

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Cannot shrink to target comp_size {targetCompSize} (got {comp.Length}): no replaceable bytes");

        byte[] trial = (byte[])padded.Clone();
        int lastN = -1;
        for (int n = 0; n < candidates.Count; n++)
        {
            trial[candidates[n]] = 0x20;
            int c = CompressionUtils.Lz4Compress(trial).Length;
            if (c == targetCompSize)
                return (byte[])trial.Clone();
            if (c < targetCompSize)
            {
                lastN = n;
                break;
            }
        }

        if (lastN < 0)
            throw new InvalidOperationException(
                $"Cannot match target comp_size {targetCompSize} (got {comp.Length}): exhausted candidates");

        for (int revert = 1; revert < Math.Min(lastN + 2, 200); revert++)
        {
            byte[] trial2 = (byte[])padded.Clone();
            int applyCount = lastN + 1 - revert;
            for (int i = 0; i < applyCount; i++)
                trial2[candidates[i]] = 0x20;
            int c = CompressionUtils.Lz4Compress(trial2).Length;
            if (c == targetCompSize)
                return trial2;
        }

        throw new InvalidOperationException(
            $"Cannot match target comp_size {targetCompSize} after shrink scan");
    }

    // ── CSS size matching (uses <!-- --> like CentreHUD) ──────────────

    /// <summary>
    /// Size-matches CSS content using a trailing &lt;!-- --&gt; comment (matches CentreHUD's approach).
    /// Used for HUD CSS files in archive 0012.
    /// </summary>
    public static byte[] MatchCompressedSizeCss(byte[] plaintext, int targetCompSize, int targetOrigSize)
    {
        byte[] padded = plaintext.Length > targetOrigSize
            ? plaintext[..targetOrigSize]
            : PadToOrigSize(plaintext, targetOrigSize);

        byte[] comp = CompressionUtils.Lz4Compress(padded);
        if (comp.Length == targetCompSize)
            return padded;

        if (comp.Length > targetCompSize)
        {
            int overBy = comp.Length - targetCompSize;
            throw new InvalidOperationException(
                $"CSS file exceeds slot by {overBy} bytes ({comp.Length} / {targetCompSize}). " +
                "The compacted CSS is too large for the archive slot.");
        }

        // Inflate with a real CSS comment: the 2.04 UI parser reads these files directly, and an
        // HTML-style comment full of random braces/quotes can corrupt the rules around it.
        var result = InflateByRandomCommentPadding(plaintext, targetCompSize, targetOrigSize, css: true);
        if (result != null) return result;

        throw new InvalidOperationException(
            $"Cannot match CSS target comp_size {targetCompSize} (got {comp.Length}). " +
            "Could not find exact size match after 10 attempts.");
    }

    // ── Core write ──────────────────────────────────────────────────

    public static void UpdateEntry(PazEntry entry, byte[] payload)
    {
        UpdateEntryAt(entry.PazFile, entry.Offset, payload);
    }

    /// <summary>
    /// Writes encrypted payload bytes at <paramref name="offset"/> inside an existing <c>.paz</c> file
    /// (e.g. a copy of the game archive for export, without modifying the live install).
    /// </summary>
    public static void UpdateEntryAt(string pazFilePath, long offset, byte[] payload)
    {
        var restoreTs = SaveTimestamps(pazFilePath);
        using (var fs = new FileStream(pazFilePath, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            fs.Write(payload);
        }
        restoreTs();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static byte[] ConcatBytes(params byte[][] arrays)
    {
        int total = arrays.Sum(a => a.Length);
        byte[] result = new byte[total];
        int offset = 0;
        foreach (var arr in arrays)
        {
            Array.Copy(arr, 0, result, offset, arr.Length);
            offset += arr.Length;
        }
        return result;
    }
}
