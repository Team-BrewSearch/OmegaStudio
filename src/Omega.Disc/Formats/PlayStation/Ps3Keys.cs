using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Omega.Disc.IO;
namespace Omega.Disc.Formats.PlayStation
{
    public enum Ps3KeySource
    {
        None,
        Embedded3K3Y,
        Sidecar,
    }
    public static class Ps3KeyResolver
    {
        public const int WatermarkOffset = 0xF70;
        public const int D1KeyOffset = 0xF80;
        private const string Watermark = "k3k3yEnWatermark";
        private const int MaxSidecarSize = 4096;
        public static readonly byte[] PublicKey =
        [
            0x6C, 0x88, 0x91, 0x0E, 0x63, 0x39, 0x5A, 0x6D,
            0x4C, 0x8B, 0x5A, 0x1E, 0xE9, 0x6B, 0x80, 0x3B
        ];
        public static Ps3KeyResult? Resolve(ReadOnlySpan<byte> head, string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            if (head.Length < D1KeyOffset + 0x20)
            {
                head = ReadHead(path);
            }
            if (HasWatermark(head))
            {
                return new Ps3KeyResult(DeriveD1(head), Ps3KeySource.Embedded3K3Y);
            }
            byte[]? sidecar = ReadSidecar(path);
            if (sidecar is not null)
            {
                return new Ps3KeyResult(sidecar, Ps3KeySource.Sidecar);
            }
            if (IsPlain(path))
            {
                return null;
            }
            return new Ps3KeyResult([], Ps3KeySource.None);
        }
        public static bool HasWatermark(ReadOnlySpan<byte> head)
        {
            if (head.Length < WatermarkOffset + Watermark.Length)
            {
                return false;
            }
            return head.Slice(WatermarkOffset, Watermark.Length).SequenceEqual(Encoding.ASCII.GetBytes(Watermark));
        }
        public static bool IsPlain(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            return path.EndsWith(".dec.iso", StringComparison.OrdinalIgnoreCase);
        }
        public static string SidecarPath(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            return IsPlain(path)
                ? path[..^".dec.iso".Length] + ".dkey"
                : path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
                    ? path[..^".iso".Length] + ".dkey"
                    : path + ".dkey";
        }
        public static byte[] DeriveD1(ReadOnlySpan<byte> head)
        {
            if (head.Length < D1KeyOffset + 0x10)
            {
                throw new InvalidDataException(
                    $"a 3k3y image carries its key at 0x{D1KeyOffset:X}, and only {head.Length} bytes were read");
            }
            byte[] encrypted = head.Slice(D1KeyOffset, 0x10).ToArray();
            byte[] derived = new byte[0x10];
            for (int i = 0; i < 0x10; i++)
            {
                derived[i] = (byte)(encrypted[i] ^ PublicKey[i]);
            }
            return derived;
        }
        private static byte[]? ReadSidecar(string path)
        {
            string sidecar = SidecarPath(path);
            if (!File.Exists(sidecar))
            {
                return null;
            }
            long size = new FileInfo(sidecar).Length;
            if (size > MaxSidecarSize)
            {
                throw new InvalidDataException(
                    $"the key file beside the image is {size} bytes, and a PS3 key is 32 hex digits");
            }
            string text = File.ReadAllText(sidecar).Trim();
            if (text.Length != 32)
            {
                throw new InvalidDataException(
                    $"the key file beside the image holds {text.Length} characters, and a PS3 key is 32 hex digits");
            }
            byte[] key = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                if (!byte.TryParse(
                    text.AsSpan(i * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out byte part))
                {
                    throw new InvalidDataException(
                        $"the key file beside the image is not 32 hexadecimal digits: {text}");
                }
                key[i] = part;
            }
            return key;
        }
        private static byte[] ReadHead(string path)
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] head = new byte[D1KeyOffset + 0x20];
            int read = file.Read(head, 0, head.Length);
            return read == head.Length ? head : head.AsSpan(0, read).ToArray();
        }
    }
    public sealed record Ps3KeyResult(byte[] Key, Ps3KeySource Source);
}
