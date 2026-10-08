using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>Reads PE export tables statically. Exports are not C++ method signatures.</summary>
internal static class NativeExports
{
    internal sealed record Export(uint Ordinal, string? Name, uint Rva, string? Forwarder);

    internal static IReadOnlyList<Export> Read(string file)
    {
        using var stream = File.OpenRead(file);
        using var pe = new PEReader(stream);
        var header = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("Not a PE image");
        var directory = header.ExportTableDirectory;
        if (directory.RelativeVirtualAddress == 0 || directory.Size == 0) return Array.Empty<Export>();
        var sections = pe.PEHeaders.SectionHeaders;
        long Offset(uint rva)
        {
            foreach (var s in sections)
            {
                var start = (uint)s.VirtualAddress;
                var size = (uint)Math.Max(s.VirtualSize, s.SizeOfRawData);
                if (rva >= start && (ulong)rva - start < size)
                {
                    var offset = (long)s.PointerToRawData + (rva - start);
                    if (offset >= 0 && offset < stream.Length) return offset;
                }
            }
            throw new InvalidDataException($"Invalid export RVA: 0x{rva:X}");
        }
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        uint U32(uint rva)
        {
            stream.Position = Offset(rva);
            return reader.ReadUInt32();
        }
        ushort U16(uint rva)
        {
            stream.Position = Offset(rva);
            return reader.ReadUInt16();
        }
        string Str(uint rva)
        {
            stream.Position = Offset(rva);
            var bytes = new List<byte>();
            for (var i = 0; i < 4096; i++)
            {
                var b = reader.ReadByte();
                if (b == 0) return Encoding.ASCII.GetString(bytes.ToArray());
                bytes.Add(b);
            }
            throw new InvalidDataException("Export string exceeds 4096 bytes");
        }
        var startRva = (uint)directory.RelativeVirtualAddress;
        var baseOrdinal = U32(startRva + 16);
        var functionCount = U32(startRva + 20);
        var nameCount = U32(startRva + 24);
        var functions = U32(startRva + 28);
        var names = U32(startRva + 32);
        var ordinals = U32(startRva + 36);
        if (functionCount > 1_000_000 || nameCount > functionCount)
            throw new InvalidDataException("Invalid export table counts");
        var byOrdinal = new Dictionary<uint, string>();
        for (uint i = 0; i < nameCount; i++)
        {
            var index = U16(checked(ordinals + i * 2));
            if (index >= functionCount) throw new InvalidDataException("Invalid export ordinal");
            byOrdinal[index] = Str(U32(checked(names + i * 4)));
        }
        var results = new List<Export>();
        for (uint i = 0; i < functionCount; i++)
        {
            var rva = U32(checked(functions + i * 4));
            if (rva == 0) continue;
            var forwarded = (ulong)rva >= startRva && (ulong)rva < (ulong)startRva + (uint)directory.Size;
            results.Add(new Export(checked(baseOrdinal + i),
                byOrdinal.GetValueOrDefault(i), rva, forwarded ? Str(rva) : null));
        }
        return results;
    }

    internal static void WriteJson(string file, string output)
    {
        var exports = Read(file);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            source = Path.GetFullPath(file),
            kind = "pe-export-table",
            note = "Export names may be mangled; ordinals and addresses are not callable API contracts.",
            exports
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Indexed {exports.Count} exports");
    }
}
