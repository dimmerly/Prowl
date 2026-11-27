// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Prowl.Aperture.Encoders;

/// <summary>
/// Writes OpenEXR: one part, scanlines, half float RGB or RGBA, losslessly compressed with ZIP in blocks of sixteen
/// rows (or left uncompressed at <see cref="CompressionEffort.None"/>). The layout every EXR reader takes.
/// </summary>
internal sealed class ExrEncoder : IImageEncoder
{
    private const int Magic = 20000630;
    private const int RowsPerZipBlock = 16;
    private const byte NoCompression = 0, ZipCompression = 3;
    private const int HalfType = 1;

    /// <inheritdoc />
    public ImageFormat Format => ImageFormat.Exr;

    /// <inheritdoc />
    public string FileExtension => ".exr";

    /// <inheritdoc />
    public IReadOnlyList<PixelFormat> SupportedPixelFormats { get; } = [PixelFormat.RgbF16, PixelFormat.RgbaF16];

    /// <inheritdoc />
    public PixelFormat PreferredPixelFormat => PixelFormat.RgbaF16;

    /// <inheritdoc />
    public bool TryEncode(ImageFrame frame, EncodeOptions options, Stream destination, out ApertureError error)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(destination);

        if (frame.PixelFormat is not (PixelFormat.RgbF16 or PixelFormat.RgbaF16))
        {
            error = ApertureError.UnsupportedFeature;
            return false;
        }
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            error = ApertureError.InvalidDimensions;
            return false;
        }

        int width = frame.Width, height = frame.Height;
        int channels = frame.PixelFormat == PixelFormat.RgbaF16 ? 4 : 3;
        bool zip = options.Effort != CompressionEffort.None;
        int rowsPerBlock = zip ? RowsPerZipBlock : 1;
        int blocks = (height + rowsPerBlock - 1) / rowsPerBlock;

        using MemoryStream header = new();
        WriteHeader(header, width, height, channels, zip ? ZipCompression : NoCompression);

        // Each block as stored: its rows one after another, each row its channels in name order (A, B, G, R), each
        // channel the row's halves.
        var chunks = new byte[blocks][];
        int rowBytes = width * channels * 2;
        byte[] raw = new byte[rowBytes * rowsPerBlock];
        for (int b = 0; b < blocks; b++)
        {
            int first = b * rowsPerBlock, rows = Math.Min(rowsPerBlock, height - first);
            int at = 0;
            for (int r = 0; r < rows; r++)
            {
                int y = first + r;
                ReadOnlySpan<ushort> source = frame.GetRowAs<ushort>(options.FlipVertically ? height - 1 - y : y)[..(width * channels)];
                // Name order: alpha, blue, green, red
                ReadOnlySpan<int> order = channels == 4 ? [3, 2, 1, 0] : [2, 1, 0];
                foreach (int c in order)
                {
                    for (int x = 0; x < width; x++)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at), source[x * channels + c]);
                        at += 2;
                    }
                }
            }
            chunks[b] = zip ? Zip(raw.AsSpan(0, at), Level(options.Effort)) : raw.AsSpan(0, at).ToArray();
        }

        // Header, then where each block starts, then the blocks with their first row and size
        long offset = header.Length + 8L * blocks;
        Span<byte> word = stackalloc byte[8];
        destination.Write(header.GetBuffer(), 0, (int)header.Length);
        for (int b = 0; b < blocks; b++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(word, (ulong)offset);
            destination.Write(word);
            offset += 8 + chunks[b].Length;
        }
        for (int b = 0; b < blocks; b++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(word, b * rowsPerBlock);
            BinaryPrimitives.WriteInt32LittleEndian(word[4..], chunks[b].Length);
            destination.Write(word);
            destination.Write(chunks[b]);
        }

        error = ApertureError.None;
        return true;
    }

    private static void WriteHeader(Stream s, int width, int height, int channels, byte compression)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(word, Magic);
        s.Write(word);
        // Version 2, single part scanlines
        BinaryPrimitives.WriteInt32LittleEndian(word, 2);
        s.Write(word);

        using (MemoryStream list = new())
        {
            foreach (string name in channels == 4 ? new[] { "A", "B", "G", "R" } : ["B", "G", "R"])
            {
                list.Write(Encoding.ASCII.GetBytes(name));
                list.WriteByte(0);
                WriteInt(list, HalfType);
                list.Write([0, 0, 0, 0]);   // pLinear, reserved
                WriteInt(list, 1);          // x sampling
                WriteInt(list, 1);          // y sampling
            }
            list.WriteByte(0);
            Attribute(s, "channels", "chlist", list.ToArray());
        }

        Attribute(s, "compression", "compression", [compression]);
        byte[] box = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(box.AsSpan(8), width - 1);
        BinaryPrimitives.WriteInt32LittleEndian(box.AsSpan(12), height - 1);
        Attribute(s, "dataWindow", "box2i", box);
        Attribute(s, "displayWindow", "box2i", box);
        Attribute(s, "lineOrder", "lineOrder", [0]);   // increasing y
        Attribute(s, "pixelAspectRatio", "float", BitConverter.GetBytes(1f));
        Attribute(s, "screenWindowCenter", "v2f", new byte[8]);
        Attribute(s, "screenWindowWidth", "float", BitConverter.GetBytes(1f));
        s.WriteByte(0);
    }

    private static void Attribute(Stream s, string name, string type, byte[] value)
    {
        s.Write(Encoding.ASCII.GetBytes(name));
        s.WriteByte(0);
        s.Write(Encoding.ASCII.GetBytes(type));
        s.WriteByte(0);
        WriteInt(s, value.Length);
        s.Write(value);
    }

    private static void WriteInt(Stream s, int value)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(word, value);
        s.Write(word);
    }

    // The bytes split into even and odd halves, each byte stored as its difference from the one before, then deflated
    private static byte[] Zip(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        byte[] split = new byte[data.Length];
        int first = 0, second = (data.Length + 1) / 2;
        for (int i = 0; i < data.Length; i++)
            split[(i & 1) == 0 ? first++ : second++] = data[i];
        for (int i = split.Length - 1; i > 0; i--)
            split[i] = (byte)(split[i] - split[i - 1] + 128);

        using MemoryStream compressed = new();
        using (ZLibStream deflate = new(compressed, level, leaveOpen: true))
            deflate.Write(split);
        return compressed.ToArray();
    }

    private static CompressionLevel Level(CompressionEffort effort) => effort switch
    {
        CompressionEffort.Fastest => CompressionLevel.Fastest,
        CompressionEffort.Smallest => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };
}
