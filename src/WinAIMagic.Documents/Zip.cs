using System.IO.Compression;
using System.Text;

namespace WinAIMagic.Documents;

public sealed class ZipEntry(string name, byte[] data)
{
    public string Name { get; } = name;
    public byte[] Data { get; set; } = data;
}

/// A .docx is a zip file. This reads stored and deflated entries and writes deflated ones, which is all Word needs.
public static class Zip
{
    public sealed class Failure(string text) : Exception(text);

    /// A template comes from elsewhere: a small file must not unpack into all memory.
    private const long MaxTotalSize = 4L << 30;

    public static List<ZipEntry> Read(byte[] data)
    {
        if (data.Length < 22) throw new Failure(L("Dit is geen geldig Word-bestand."));
        long U16(long at) => data[at] | (long)data[at + 1] << 8;
        long U32(long at) => U16(at) | U16(at + 2) << 16;

        long end = data.Length - 22;
        long lowest = Math.Max(0, data.Length - 65_557);
        while (end >= lowest && U32(end) != 0x0605_4b50) end--;
        if (end < lowest) throw new Failure(L("Dit is geen geldig Word-bestand."));

        long count = U16(end + 10);
        long at = U32(end + 16);
        long total = 0;
        var output = new List<ZipEntry>();
        for (long n = 0; n < count; n++)
        {
            if (at + 46 > data.Length || U32(at) != 0x0201_4b50) throw new Failure(L("Dit is geen geldig Word-bestand."));
            long flags = U16(at + 8), method = U16(at + 10), crc = U32(at + 16);
            long packed = U32(at + 20), size = U32(at + 24);
            long nameLength = U16(at + 28), extra = U16(at + 30), comment = U16(at + 32), local = U32(at + 42);
            if ((flags & 1) != 0) throw new Failure(L("Dit Word-bestand is beveiligd met een wachtwoord."));
            if (at + 46 + nameLength > data.Length || local + 30 > data.Length || U32(local) != 0x0403_4b50)
                throw new Failure(L("Dit is geen geldig Word-bestand."));
            var name = Encoding.UTF8.GetString(data, (int)(at + 46), (int)nameLength);
            long start = local + 30 + U16(local + 26) + U16(local + 28);
            if (start + packed > data.Length) throw new Failure(L("Dit is geen geldig Word-bestand."));
            total += size;
            // one .NET array holds less than Swift's Data: too big for it is too big
            if (total > MaxTotalSize || size > Array.MaxLength) throw new Failure(L("Dit Word-bestand is te groot om uit te pakken."));
            byte[] content = method switch
            {
                0 => data.AsSpan((int)start, (int)packed).ToArray(),
                8 => Inflate(data, (int)start, (int)packed, (int)size),
                _ => throw new Failure(L("Dit Word-bestand gebruikt een onbekende compressie.")),
            };
            if (Crc32(content) != (uint)crc) throw new Failure(L("Dit Word-bestand is beschadigd."));
            if (!name.HasSuffix("/")) output.Add(new ZipEntry(name, content));
            at += 46 + nameLength + extra + comment;
        }
        return output;
    }

    public static byte[] Write(IEnumerable<ZipEntry> entries)
    {
        var output = new MemoryStream();
        var central = new MemoryStream();
        static void Put16(MemoryStream d, long v) { d.WriteByte((byte)(v & 0xff)); d.WriteByte((byte)(v >> 8 & 0xff)); }
        static void Put32(MemoryStream d, long v) { Put16(d, v & 0xffff); Put16(d, v >> 16 & 0xffff); }

        long count = 0;
        foreach (var entry in entries)
        {
            count++;
            var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
            var content = entry.Data;
            long crc = Crc32(content);
            var packed = Deflate(content);
            var body = packed ?? content;
            long offset = output.Length;
            Put32(output, 0x0403_4b50);
            Put16(output, 20); Put16(output, 0x0800); Put16(output, packed == null ? 0 : 8);
            Put16(output, 0); Put16(output, 0x21);                       // 1 January 1980: the bytes depend only on the content
            Put32(output, crc); Put32(output, body.Length); Put32(output, content.Length);
            Put16(output, nameBytes.Length); Put16(output, 0);
            output.Write(nameBytes);
            output.Write(body);

            Put32(central, 0x0201_4b50);
            Put16(central, 20); Put16(central, 20); Put16(central, 0x0800); Put16(central, packed == null ? 0 : 8);
            Put16(central, 0); Put16(central, 0x21);
            Put32(central, crc); Put32(central, body.Length); Put32(central, content.Length);
            Put16(central, nameBytes.Length); Put16(central, 0); Put16(central, 0);
            Put16(central, 0); Put16(central, 0); Put32(central, 0); Put32(central, offset);
            central.Write(nameBytes);
        }
        long start = output.Length;
        central.WriteTo(output);
        Put32(output, 0x0605_4b50);
        Put16(output, 0); Put16(output, 0);
        Put16(output, count); Put16(output, count);
        Put32(output, central.Length); Put32(output, start);
        Put16(output, 0);
        return output.ToArray();
    }

    private static byte[] Inflate(byte[] data, int start, int packed, int size)
    {
        if (size == 0) return [];
        var output = new byte[size];
        int written = 0;
        try
        {
            using var stream = new DeflateStream(new MemoryStream(data, start, packed, false), CompressionMode.Decompress);
            while (written < size)
            {
                int n = stream.Read(output, written, size - written);
                if (n == 0) break;
                written += n;
            }
        }
        catch (InvalidDataException)
        {
            throw new Failure(L("Dit Word-bestand is beschadigd."));
        }
        if (written != size) throw new Failure(L("Dit Word-bestand is beschadigd."));
        return output;
    }

    /// The deflated bytes, or null when storing the entry is smaller.
    private static byte[]? Deflate(byte[] content)
    {
        if (content.Length == 0) return null;
        var output = new MemoryStream();
        using (var stream = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) stream.Write(content);
        return output.Length < content.Length ? output.ToArray() : null;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++) c = (c & 1) == 1 ? 0xedb8_8320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Crc32(byte[] data)
    {
        uint c = 0xffff_ffff;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xff] ^ (c >> 8);
        return c ^ 0xffff_ffff;
    }
}
