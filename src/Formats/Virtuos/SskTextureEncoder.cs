using SMath = System.Math;
namespace Virtuos.Formats;
public static class SskTextureEncoder
{
    private const ushort MagentaRgb555 = 0x7C1F;
    public static byte[] Encode(SskTexture source, byte[] rgbaPixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgbaPixels);
        int fmt = source.Format is 1 or 2 or 3 or 4 or 5 or 6 or 7 ? source.Format : 4;
        int paddedW = DsColor.RoundTextureSize(width);
        int paddedH = DsColor.RoundTextureSize(height);
        byte[] padded = PadRgba(rgbaPixels, width, height, paddedW, paddedH);
        byte[] texel;
        byte[]? cmp = null;
        ushort[] palette;
        switch (fmt)
        {
            case 7:
                texel = EncodeDirect(padded, paddedW, paddedH);
                palette = [];
                break;
            case 5:
                (texel, cmp, palette) = EncodeTex4x4(padded, paddedW, paddedH);
                break;
            case 1:
                texel = EncodeAlphaIndexed(padded, paddedW, paddedH, maxColors: 32, alphaBits: 3, out palette);
                break;
            case 6:
                texel = EncodeAlphaIndexed(padded, paddedW, paddedH, maxColors: 8, alphaBits: 5, out palette);
                break;
            default:
                int maxColors = fmt switch { 2 => 4, 3 => 16, _ => 256 };
                (texel, palette) = EncodePalette(padded, paddedW, paddedH, maxColors);
                break;
        }
        return BuildBlob(
            fmt, width, height, paddedW, paddedH, texel, cmp, palette,
            source.ColorZeroTransparent, source.TextureParam, source.FormatHint);
    }
    private static byte[] PadRgba(byte[] src, int w, int h, int paddedW, int paddedH)
    {
        if (w == paddedW && h == paddedH)
        {
            return src;
        }
        byte[] dst = new byte[paddedW * paddedH * 4];
        for (int y = 0; y < paddedH; y++)
        {
            int sy = SMath.Min(y, h - 1);
            for (int x = 0; x < paddedW; x++)
            {
                int sx = SMath.Min(x, w - 1);
                int srcIdx = ((sy * w) + sx) * 4;
                int dstIdx = ((y * paddedW) + x) * 4;
                dst[dstIdx] = src[srcIdx];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
                dst[dstIdx + 3] = src[srcIdx + 3];
            }
        }
        return dst;
    }
    private static byte[] EncodeDirect(byte[] rgba, int w, int h)
    {
        byte[] pix = new byte[w * h * 2];
        for (int i = 0; i < w * h; i++)
        {
            ushort c = DsColor.ToRgb555(rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2]);
            if (rgba[(i * 4) + 3] >= 128)
            {
                c |= 0x8000;
            }
            pix[i * 2] = (byte)(c & 0xFF);
            pix[(i * 2) + 1] = (byte)(c >> 8);
        }
        return pix;
    }
    private static (byte[] Indices, ushort[] Palette) EncodePalette(byte[] rgba, int w, int h, int maxColors)
    {
        int n = w * h;
        bool hasAlpha = false;
        for (int i = 0; i < n && !hasAlpha; i++)
        {
            hasAlpha = rgba[(i * 4) + 3] < 128;
        }
        int baseSlot = hasAlpha ? 1 : 0;
        int budget = SMath.Max(1, maxColors - baseSlot);
        Dictionary<ushort, int> weighted = [];
        for (int i = 0; i < n; i++)
        {
            if (rgba[(i * 4) + 3] < 128)
            {
                continue;
            }
            ushort c = DsColor.ToRgb555(rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2]);
            weighted[c] = weighted.GetValueOrDefault(c) + 1;
        }
        List<ushort> paletteColors = MedianCut(weighted, budget);
        ushort[] palette = new ushort[maxColors];
        if (hasAlpha)
        {
            palette[0] = MagentaRgb555;
        }
        for (int k = 0; k < paletteColors.Count; k++)
        {
            palette[baseSlot + k] = paletteColors[k];
        }
        byte[] indices = new byte[n];
        for (int i = 0; i < n; i++)
        {
            if (rgba[(i * 4) + 3] < 128)
            {
                indices[i] = 0;
                continue;
            }
            ushort c = DsColor.ToRgb555(rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2]);
            indices[i] = (byte)(baseSlot + NearestColorIndex(paletteColors, c));
        }
        return (PackIndices(indices, maxColors), palette);
    }
    private static byte[] PackIndices(byte[] indices, int maxColors)
    {
        int bits = maxColors switch { <= 4 => 2, <= 16 => 4, _ => 8 };
        if (bits == 8)
        {
            return indices;
        }
        int perByte = 8 / bits;
        byte[] texel = new byte[(indices.Length + perByte - 1) / perByte];
        for (int i = 0; i < indices.Length; i++)
        {
            int shift = i % perByte * bits;
            texel[i / perByte] |= (byte)((indices[i] & ((1 << bits) - 1)) << shift);
        }
        return texel;
    }
    private static byte[] EncodeAlphaIndexed(
        byte[] rgba, int w, int h, int maxColors, int alphaBits, out ushort[] palette)
    {
        int n = w * h;
        int indexBits = alphaBits == 3 ? 5 : 3;
        Dictionary<ushort, int> weighted = [];
        for (int i = 0; i < n; i++)
        {
            ushort c = DsColor.ToRgb555(rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2]);
            weighted[c] = weighted.GetValueOrDefault(c) + 1;
        }
        List<ushort> paletteColors = MedianCut(weighted, maxColors);
        palette = new ushort[maxColors];
        for (int k = 0; k < paletteColors.Count; k++)
        {
            palette[k] = paletteColors[k];
        }
        int alphaLevels = 1 << alphaBits;
        byte[] texel = new byte[n];
        for (int i = 0; i < n; i++)
        {
            ushort c = DsColor.ToRgb555(rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2]);
            int index = NearestColorIndex(paletteColors, c);
            int target = rgba[(i * 4) + 3];
            int bestLevel = 0, bestErr = int.MaxValue;
            for (int level = 0; level < alphaLevels; level++)
            {
                int err = SMath.Abs(ExpandAlpha(level, alphaBits) - target);
                if (err < bestErr)
                {
                    bestErr = err;
                    bestLevel = level;
                }
            }
            texel[i] = (byte)((index & ((1 << indexBits) - 1)) | (bestLevel << indexBits));
        }
        return texel;
    }
    private static int ExpandAlpha(int level, int alphaBits)
    {
        if (alphaBits == 3)
        {
            int a5 = (level << 2) | (level >> 1);
            return ((a5 * 510) + 31) / 62;
        }
        return ((level * 510) + 31) / 62;
    }
    private static (byte[] Texel, byte[] Cmp, ushort[] Palette) EncodeTex4x4(byte[] rgba, int paddedW, int paddedH)
    {
        int blocksX = SMath.Max(1, paddedW / 4);
        int blocksY = SMath.Max(1, paddedH / 4);
        int blockCount = blocksX * blocksY;
        byte[] texel = new byte[blockCount * 4];
        byte[] cmp = new byte[blockCount * 2];
        List<ushort> palette = [];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                EncodeTex4x4Block(rgba, paddedW, bx, by, texel, cmp, bx + (by * blocksX), palette);
            }
        }
        return (texel, cmp, [.. palette]);
    }
    private static void EncodeTex4x4Block(
        byte[] rgba, int paddedW, int bx, int by, byte[] texel, byte[] cmp, int blockIndex, List<ushort> palette)
    {
        (byte R, byte G, byte B, byte A)[] px = new (byte, byte, byte, byte)[16];
        for (int ly = 0; ly < 4; ly++)
        {
            for (int lx = 0; lx < 4; lx++)
            {
                int x = (bx * 4) + lx, y = (by * 4) + ly;
                int srcIdx = ((y * paddedW) + x) * 4;
                px[lx + (ly * 4)] = (rgba[srcIdx], rgba[srcIdx + 1], rgba[srcIdx + 2], rgba[srcIdx + 3]);
            }
        }
        bool blockHasAlpha = Array.Exists(px, p => p.A < 128);
        ((byte R, byte G, byte B) c0, (byte R, byte G, byte B) c1) = PickEndpoints(px);
        if ((palette.Count & 1) != 0)
        {
            palette.Add(palette[^1]);
        }
        int baseIndex = palette.Count;
        if ((baseIndex >> 1) > 0x3FFF)
        {
            throw new InvalidDataException(
                "This image needs more TEX4x4 palette entries than the format's 14-bit block " +
                "address can reach. Use a smaller image, or one with fewer distinct colours.");
        }
        ushort code;
        if (blockHasAlpha)
        {
            (byte R, byte G, byte B) blend = DsColor.Blend(c0, c1, 4);
            palette.Add(DsColor.ToRgb555(c0.R, c0.G, c0.B));
            palette.Add(DsColor.ToRgb555(c1.R, c1.G, c1.B));
            code = 0x4000;
            WriteBlockIndices(texel, blockIndex, px, [c0, c1, blend], transparentSlot: 3);
        }
        else
        {
            (byte R, byte G, byte B) blend3 = DsColor.Blend(c0, c1, 3);
            (byte R, byte G, byte B) blend5 = DsColor.Blend(c0, c1, 5);
            double error = ReconstructionError(px, [c0, c1, blend3, blend5]);
            const double cheapModeThreshold = 24.0 * 16;
            if (error <= cheapModeThreshold)
            {
                palette.Add(DsColor.ToRgb555(c0.R, c0.G, c0.B));
                palette.Add(DsColor.ToRgb555(c1.R, c1.G, c1.B));
                code = 0xC000;
                WriteBlockIndices(texel, blockIndex, px, [c0, c1, blend3, blend5], transparentSlot: -1);
            }
            else
            {
                (byte R, byte G, byte B)[] four = PickFourColors(px);
                foreach ((byte R, byte G, byte B) in four)
                {
                    palette.Add(DsColor.ToRgb555(R, G, B));
                }
                code = 0x8000;
                WriteBlockIndices(texel, blockIndex, px, four, transparentSlot: -1);
            }
        }
        code |= (ushort)(baseIndex >> 1);
        cmp[blockIndex * 2] = (byte)(code & 0xFF);
        cmp[(blockIndex * 2) + 1] = (byte)(code >> 8);
    }
    private static ((byte R, byte G, byte B) C0, (byte R, byte G, byte B) C1) PickEndpoints(
        (byte R, byte G, byte B, byte A)[] px)
    {
        List<(byte R, byte G, byte B)> opaque = [];
        foreach ((byte R, byte G, byte B, byte A) in px)
        {
            if (A >= 128)
            {
                opaque.Add((R, G, B));
            }
        }
        if (opaque.Count == 0)
        {
            return ((0, 0, 0), (0, 0, 0));
        }
        if (opaque.Count == 1)
        {
            return (opaque[0], opaque[0]);
        }
        (byte R, byte G, byte B) c1 = FarthestFrom(opaque, opaque[0]);
        (byte R, byte G, byte B) c0 = FarthestFrom(opaque, c1);
        return (c0, c1);
    }
    private static (byte R, byte G, byte B) FarthestFrom(
        List<(byte R, byte G, byte B)> colors, (byte R, byte G, byte B) from)
    {
        (byte R, byte G, byte B) best = colors[0];
        int bestDist = -1;
        foreach ((byte R, byte G, byte B) c in colors)
        {
            int d = SquaredDistance(c, from);
            if (d > bestDist)
            {
                bestDist = d;
                best = c;
            }
        }
        return best;
    }
    private static (byte R, byte G, byte B)[] PickFourColors((byte R, byte G, byte B, byte A)[] px)
    {
        List<(byte R, byte G, byte B)> opaque = [];
        foreach ((byte R, byte G, byte B, byte A) in px)
        {
            if (A >= 128)
            {
                opaque.Add((R, G, B));
            }
        }
        if (opaque.Count == 0)
        {
            return [(0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0)];
        }
        List<(byte R, byte G, byte B)> chosen = [opaque[0]];
        while (chosen.Count < 4 && chosen.Count < opaque.Count)
        {
            (byte R, byte G, byte B) best = opaque[0];
            int bestMinDist = -1;
            foreach ((byte R, byte G, byte B) c in opaque)
            {
                int minDist = int.MaxValue;
                foreach ((byte R, byte G, byte B) s in chosen)
                {
                    minDist = SMath.Min(minDist, SquaredDistance(c, s));
                }
                if (minDist > bestMinDist)
                {
                    bestMinDist = minDist;
                    best = c;
                }
            }
            chosen.Add(best);
        }
        while (chosen.Count < 4)
        {
            chosen.Add(chosen[^1]);
        }
        return [.. chosen];
    }
    private static double ReconstructionError(
        (byte R, byte G, byte B, byte A)[] px, (byte R, byte G, byte B)[] candidates)
    {
        double total = 0;
        foreach ((byte R, byte G, byte B, byte A) in px)
        {
            if (A < 128)
            {
                continue;
            }
            int best = int.MaxValue;
            foreach ((byte R, byte G, byte B) c in candidates)
            {
                best = SMath.Min(best, SquaredDistance((R, G, B), c));
            }
            total += best;
        }
        return total;
    }
    private static void WriteBlockIndices(
        byte[] texel, int blockIndex, (byte R, byte G, byte B, byte A)[] px,
        (byte R, byte G, byte B)[] candidates, int transparentSlot)
    {
        uint word = 0;
        for (int p = 0; p < 16; p++)
        {
            int idx = transparentSlot >= 0 && px[p].A < 128
                ? transparentSlot
                : NearestCandidateIndex((px[p].R, px[p].G, px[p].B), candidates);
            word |= (uint)(idx & 0x3) << (2 * p);
        }
        int off = blockIndex * 4;
        texel[off] = (byte)word;
        texel[off + 1] = (byte)(word >> 8);
        texel[off + 2] = (byte)(word >> 16);
        texel[off + 3] = (byte)(word >> 24);
    }
    private static int NearestCandidateIndex((byte R, byte G, byte B) color, (byte R, byte G, byte B)[] candidates)
    {
        int best = 0, bestDist = int.MaxValue;
        for (int i = 0; i < candidates.Length; i++)
        {
            int d = SquaredDistance(color, candidates[i]);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }
    private static int SquaredDistance((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return (dr * dr) + (dg * dg) + (db * db);
    }
    private static List<ushort> MedianCut(Dictionary<ushort, int> weighted, int maxColors)
    {
        if (weighted.Count == 0)
        {
            return [];
        }
        if (weighted.Count <= maxColors)
        {
            return [.. weighted.Keys];
        }
        List<List<(ushort Color, int Weight)>> buckets = [[.. weighted.Select(kv => (kv.Key, kv.Value))]];
        while (buckets.Count < maxColors)
        {
            int splitIdx = -1, bestRange = 0, bestChannel = 0;
            for (int bi = 0; bi < buckets.Count; bi++)
            {
                List<(ushort Color, int Weight)> b = buckets[bi];
                if (b.Count <= 1)
                {
                    continue;
                }
                for (int ch = 0; ch < 3; ch++)
                {
                    int shift = ch * 5;
                    int min = int.MaxValue, max = int.MinValue;
                    foreach ((ushort color, _) in b)
                    {
                        int v = (color >> shift) & 0x1F;
                        min = SMath.Min(min, v);
                        max = SMath.Max(max, v);
                    }
                    if (max - min > bestRange)
                    {
                        bestRange = max - min;
                        splitIdx = bi;
                        bestChannel = ch;
                    }
                }
            }
            if (splitIdx < 0)
            {
                break;
            }
            List<(ushort Color, int Weight)> target = buckets[splitIdx];
            int shiftB = bestChannel * 5;
            target.Sort((a, b) => ((a.Color >> shiftB) & 0x1F).CompareTo((b.Color >> shiftB) & 0x1F));
            int totalWeight = target.Sum(t => t.Weight);
            int half = totalWeight / 2;
            int acc = 0, splitAt = 1;
            for (int k = 0; k < target.Count; k++)
            {
                acc += target[k].Weight;
                if (acc >= half)
                {
                    splitAt = SMath.Max(1, k + 1);
                    break;
                }
            }
            splitAt = SMath.Min(splitAt, target.Count - 1);
            buckets[splitIdx] = target.GetRange(0, splitAt);
            buckets.Add(target.GetRange(splitAt, target.Count - splitAt));
        }
        List<ushort> result = [];
        foreach (List<(ushort Color, int Weight)> b in buckets)
        {
            long rSum = 0, gSum = 0, bSum = 0, wSum = 0;
            foreach ((ushort color, int weight) in b)
            {
                rSum += (color & 0x1F) * weight;
                gSum += ((color >> 5) & 0x1F) * weight;
                bSum += ((color >> 10) & 0x1F) * weight;
                wSum += weight;
            }
            if (wSum == 0)
            {
                continue;
            }
            result.Add((ushort)((rSum / wSum) | ((gSum / wSum) << 5) | ((bSum / wSum) << 10)));
        }
        return result;
    }
    private static int NearestColorIndex(List<ushort> palette, ushort target)
    {
        int tr = target & 0x1F, tg = (target >> 5) & 0x1F, tb = (target >> 10) & 0x1F;
        int best = 0, bestDist = int.MaxValue;
        for (int i = 0; i < palette.Count; i++)
        {
            int c = palette[i];
            int dr = (c & 0x1F) - tr, dg = ((c >> 5) & 0x1F) - tg, db = ((c >> 10) & 0x1F) - tb;
            int dist = (dr * dr) + (dg * dg) + (db * db);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }
        return best;
    }
    private static byte[] BuildBlob(
        int fmt, int origW, int origH, int paddedW, int paddedH,
        byte[] texel, byte[]? cmp, ushort[] palette, bool colorZeroTransparent,
        ushort textureParam, ushort formatHint)
    {
        int palNumColors = palette.Length;
        int paltSectionSize = 12 + (palNumColors * 2);
        int pixDataLen = texel.Length + (cmp?.Length ?? 0);
        int imgeSectionSize = 20 + pixDataLen;
        int totalSize = 16 + paltSectionSize + imgeSectionSize;
        byte[] blob = new byte[totalSize];
        int off = 0;
        WriteBytes(blob, ref off, "NTTX"u8);
        WriteU16(blob, ref off, 0xFEFF);
        WriteU16(blob, ref off, 0x0100);
        WriteU32(blob, ref off, (uint)totalSize);
        WriteU16(blob, ref off, textureParam);
        WriteU16(blob, ref off, formatHint);
        WriteBytes(blob, ref off, "PALT"u8);
        WriteU32(blob, ref off, (uint)paltSectionSize);
        WriteU32(blob, ref off, (uint)palNumColors);
        for (int i = 0; i < palNumColors; i++)
        {
            WriteU16(blob, ref off, palette[i]);
        }
        WriteBytes(blob, ref off, "IMGE"u8);
        WriteU32(blob, ref off, (uint)imgeSectionSize);
        blob[off++] = (byte)fmt;
        blob[off++] = (byte)DsColor.Log2Floor(paddedW >> 3);
        blob[off++] = (byte)DsColor.Log2Floor(paddedH >> 3);
        blob[off++] = (byte)(colorZeroTransparent ? 1 : 0);
        WriteU16(blob, ref off, (ushort)origW);
        WriteU16(blob, ref off, (ushort)origH);
        WriteU32(blob, ref off, (uint)pixDataLen);
        texel.CopyTo(blob.AsSpan(off));
        off += texel.Length;
        cmp?.CopyTo(blob.AsSpan(off));
        return blob;
    }
    private static void WriteBytes(byte[] buf, ref int off, ReadOnlySpan<byte> src)
    {
        src.CopyTo(buf.AsSpan(off));
        off += src.Length;
    }
    private static void WriteU16(byte[] buf, ref int off, ushort v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)(v >> 8);
        off += 2;
    }
    private static void WriteU32(byte[] buf, ref int off, uint v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)((v >> 8) & 0xFF);
        buf[off + 2] = (byte)((v >> 16) & 0xFF);
        buf[off + 3] = (byte)(v >> 24);
        off += 4;
    }
}
