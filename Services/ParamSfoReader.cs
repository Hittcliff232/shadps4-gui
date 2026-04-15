using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ShadPS4Launcher.Services;

/// <summary>
/// Minimal reader for PS4 param.sfo (SFO binary format) to get TITLE and TITLE_ID.
/// </summary>
public static class ParamSfoReader
{
    private const uint SfoMagic = 0x46535000; // "\0PSF" little-endian

    public static string? GetTitle(string paramSfoPath)
    {
        try
        {
            using var fs = File.OpenRead(paramSfoPath);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x14) return null;
            var magic = br.ReadUInt32();
            if (magic != SfoMagic) return null;
            br.ReadUInt32(); // version
            var keyTableStart = br.ReadUInt32();
            var dataTableStart = br.ReadUInt32();
            var tableCount = br.ReadUInt32();
            if (tableCount == 0 || keyTableStart >= fs.Length || dataTableStart >= fs.Length)
                return null;
            for (var i = 0; i < tableCount; i++)
            {
                var keyOffset = br.ReadUInt16();
                var dataFormat = br.ReadUInt16();
                var dataLength = br.ReadUInt32();
                var dataMaxLength = br.ReadUInt32();
                var dataOffset = br.ReadUInt32();
                var key = ReadKey(fs, keyTableStart + keyOffset);
                if (key == "TITLE" && dataLength > 0 && dataTableStart + dataOffset + dataLength <= fs.Length)
                    return ReadDataString(fs, dataTableStart + dataOffset, dataLength);
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    public static string? GetTitleId(string paramSfoPath)
    {
        try
        {
            using var fs = File.OpenRead(paramSfoPath);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x14) return null;
            var magic = br.ReadUInt32();
            if (magic != SfoMagic) return null;
            br.ReadUInt32();
            var keyTableStart = br.ReadUInt32();
            var dataTableStart = br.ReadUInt32();
            var tableCount = br.ReadUInt32();
            for (var i = 0; i < tableCount; i++)
            {
                var keyOffset = br.ReadUInt16();
                br.ReadUInt16();
                var dataLength = br.ReadUInt32();
                br.ReadUInt32();
                var dataOffset = br.ReadUInt32();
                var key = ReadKey(fs, keyTableStart + keyOffset);
                if (key == "TITLE_ID" && dataLength > 0 && dataTableStart + dataOffset + dataLength <= fs.Length)
                    return ReadDataString(fs, dataTableStart + dataOffset, dataLength);
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private static string ReadKey(FileStream fs, long position)
    {
        fs.Seek(position, SeekOrigin.Begin);
        var list = new List<byte>();
        int b;
        while ((b = fs.ReadByte()) != -1 && b != 0)
            list.Add((byte)b);
        return Encoding.UTF8.GetString(list.ToArray());
    }

    private static string ReadDataString(FileStream fs, long position, uint maxLen)
    {
        fs.Seek(position, SeekOrigin.Begin);
        var bytes = new byte[Math.Min(maxLen, 512)];
        var read = fs.Read(bytes, 0, bytes.Length);
        if (read <= 0) return "";
        var end = Array.IndexOf(bytes, (byte)0, 0, read);
        if (end < 0) end = read;
        return Encoding.UTF8.GetString(bytes, 0, end).Trim();
    }
}
