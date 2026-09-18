using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Grid.Mo2.Models;

sealed class Mo2Tes4RecordGraphFixtureBuilder : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Grid.Mo2.Tes4.Tests", Guid.NewGuid().ToString("N"));

    public Mo2Tes4RecordGraphFixtureBuilder() => Directory.CreateDirectory(root);

    public async Task<string> WritePluginAsync(
        string name,
        IReadOnlyList<string> masters,
        uint tes4Flags,
        params byte[][] children)
    {
        using var headerData = new MemoryStream();
        foreach (var master in masters)
        {
            WriteSubrecord(headerData, "MAST", Encoding.Latin1.GetBytes(master + "\0"));
            WriteSubrecord(headerData, "DATA", new byte[8]);
        }

        using var output = new MemoryStream();
        WriteRecordHeader(output, "TES4", checked((uint)headerData.Length), tes4Flags, 0);
        headerData.Position = 0;
        headerData.CopyTo(output);
        foreach (var child in children) output.Write(child);
        var path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, output.ToArray());
        return path;
    }

    public static byte[] Group(int type, uint label, params byte[][] children)
    {
        var size = checked(24 + children.Sum(value => value.Length));
        using var output = new MemoryStream();
        output.Write("GRUP"u8);
        WriteUInt32(output, checked((uint)size));
        WriteUInt32(output, label);
        WriteInt32(output, type);
        output.Write(new byte[8]);
        foreach (var child in children) output.Write(child);
        return output.ToArray();
    }

    public static byte[] Record(string signature, uint formId, uint flags, bool compressed, params byte[][] subrecords)
    {
        using var raw = new MemoryStream();
        foreach (var subrecord in subrecords) raw.Write(subrecord);
        var data = raw.ToArray();
        if (compressed)
        {
            using var packed = new MemoryStream();
            WriteUInt32(packed, checked((uint)data.Length));
            using (var zlib = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(data);
            data = packed.ToArray();
            flags |= 0x0004_0000;
        }

        using var output = new MemoryStream();
        WriteRecordHeader(output, signature, checked((uint)data.Length), flags, formId);
        output.Write(data);
        return output.ToArray();
    }

    public static byte[] EditorId(string value) => Subrecord("EDID", Encoding.Latin1.GetBytes(value + "\0"));
    public static byte[] ModelPath(string value) => Subrecord("MODL", Encoding.Latin1.GetBytes(value + "\0"));
    public static byte[] FormSubrecord(string signature, uint value) => Subrecord(signature, UInt32Bytes(value));
    public static byte[] FormArraySubrecord(string signature, params uint[] values)
    {
        var data = new byte[checked(values.Length * 4)];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(index * 4), values[index]);
        return Subrecord(signature, data);
    }
    public static byte[] RawSubrecord(string signature, params byte[] data) => Subrecord(signature, data);
    public static byte[] ActorConfiguration(Mo2Tes4ActorTemplateFlags templateFlags)
    {
        var data = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18), (ushort)templateFlags);
        return Subrecord("ACBS", data);
    }
    public static byte[] MagicEffectData(
        uint associatedItem = 0,
        uint castingLight = 0,
        uint hitShader = 0,
        uint enchantShader = 0,
        uint projectile = 0,
        uint explosion = 0,
        uint castingArt = 0,
        uint hitEffectArt = 0,
        uint impactData = 0,
        uint dualCastingArt = 0,
        uint enchantArt = 0,
        uint hitVisuals = 0,
        uint enchantVisuals = 0,
        uint equipAbility = 0,
        uint imageSpaceModifier = 0,
        uint perk = 0)
    {
        var data = new byte[152];
        Write(8, associatedItem);
        Write(24, castingLight);
        Write(32, hitShader);
        Write(36, enchantShader);
        Write(72, projectile);
        Write(76, explosion);
        Write(92, castingArt);
        Write(96, hitEffectArt);
        Write(100, impactData);
        Write(108, dualCastingArt);
        Write(116, enchantArt);
        Write(120, hitVisuals);
        Write(124, enchantVisuals);
        Write(128, equipAbility);
        Write(132, imageSpaceModifier);
        Write(136, perk);
        return Subrecord("DATA", data);

        void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }
    public static byte[] EnableParent(uint reference, uint flags)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data, reference);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), flags);
        return Subrecord("XESP", data);
    }
    public static byte[] LinkedReference(uint keyword, uint reference)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data, reference);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), keyword);
        return Subrecord("XLKR", data);
    }
    public static byte[] Transform(float x, float y, float z, float rx = 0, float ry = 0, float rz = 0)
    {
        var data = new byte[24];
        WriteSingle(data, 0, x); WriteSingle(data, 4, y); WriteSingle(data, 8, z);
        WriteSingle(data, 12, rx); WriteSingle(data, 16, ry); WriteSingle(data, 20, rz);
        return Subrecord("DATA", data);
    }
    public static byte[] Scale(float value)
    {
        var data = new byte[4];
        WriteSingle(data, 0, value);
        return Subrecord("XSCL", data);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static byte[] Subrecord(string signature, byte[] data)
    {
        using var output = new MemoryStream();
        WriteSubrecord(output, signature, data);
        return output.ToArray();
    }
    private static void WriteSubrecord(Stream output, string signature, byte[] data)
    {
        output.Write(Encoding.ASCII.GetBytes(signature));
        WriteUInt16(output, checked((ushort)data.Length));
        output.Write(data);
    }
    private static void WriteRecordHeader(Stream output, string signature, uint size, uint flags, uint formId)
    {
        output.Write(Encoding.ASCII.GetBytes(signature));
        WriteUInt32(output, size);
        WriteUInt32(output, flags);
        WriteUInt32(output, formId);
        output.Write(new byte[8]);
    }
    private static byte[] UInt32Bytes(uint value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        return data;
    }
    private static void WriteSingle(Span<byte> data, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(data[offset..], BitConverter.SingleToInt32Bits(value));
    private static void WriteUInt16(Stream output, ushort value)
    {
        Span<byte> data = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(data, value); output.Write(data);
    }
    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> data = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(data, value); output.Write(data);
    }
    private static void WriteInt32(Stream output, int value)
    {
        Span<byte> data = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(data, value); output.Write(data);
    }
}
