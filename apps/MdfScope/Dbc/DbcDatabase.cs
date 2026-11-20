using System.Buffers.Binary;
using DbcParserLib;
using DbcParserLib.Model;

namespace MdfScope;

/// <summary>A DBC file prepared for decoding: messages by identifier, signals with precomputed bit layout.</summary>
internal sealed class DbcDatabase
{
    readonly Dictionary<(uint Id, bool Extended), DbcMessage> messages = new();

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public IReadOnlyCollection<DbcMessage> Messages => messages.Values;

    DbcDatabase(string path, Dbc dbc)
    {
        Path = path;
        foreach (var m in dbc.Messages)
        {
            // DBC stores extended identifiers with bit 31 set; frames carry the plain 29-bit value.
            uint id = m.IsExtID ? m.ID & 0x1FFFFFFF : m.ID;
            var message = new DbcMessage(id, m.IsExtID, m.Name, m.Comment ?? string.Empty,
                m.Signals.Select(s => new DbcSignal(s)).ToList());
            messages[(id, m.IsExtID)] = message;
        }
    }

    /// <summary>Loads a DBC; files that are not valid UTF-8 are read as Latin-1, as most DBC editors write them.</summary>
    public static DbcDatabase Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        try { text = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { text = System.Text.Encoding.Latin1.GetString(bytes); }
        return new(path, Parser.Parse(text.TrimStart('﻿')));
    }

    public DbcMessage? Find(uint id, bool extended) =>
        messages.TryGetValue((id, extended), out var m) ? m : null;
}

internal sealed class DbcMessage(uint id, bool extended, string name, string comment, IReadOnlyList<DbcSignal> signals)
{
    public uint Id => id;
    public bool IsExtended => extended;
    public string Name => name;
    public string Comment => comment;
    public IReadOnlyList<DbcSignal> Signals => signals;
    public DbcSignal? Multiplexor { get; } = signals.FirstOrDefault(s => s.IsMultiplexor);

    /// <summary>Decodes every signal present in <paramref name="data"/>; multiplexed signals only for the active group.</summary>
    public void Decode(ReadOnlySpan<byte> data, Action<DbcSignal, double> sink)
    {
        long? mux = null;
        if (Multiplexor != null && Multiplexor.TryRaw(data, out long m)) mux = m;
        foreach (var s in signals)
        {
            if (s.MultiplexGroup is int group && mux != group) continue;
            if (s.TryDecode(data, out double v)) sink(s, v);
        }
    }
}

internal sealed class DbcSignal
{
    readonly int start;
    readonly int length;
    readonly bool intel;
    readonly DbcValueType type;

    public string Name { get; }
    public string Unit { get; }
    public string Comment { get; }
    public double Factor { get; }
    public double Offset { get; }
    public IReadOnlyDictionary<int, string>? ValueTable { get; }
    public bool IsMultiplexor { get; }
    public int? MultiplexGroup { get; }

    internal DbcSignal(Signal s)
    {
        Name = s.Name;
        Unit = s.Unit ?? string.Empty;
        Comment = s.Comment ?? string.Empty;
        Factor = s.Factor;
        Offset = s.Offset;
        start = s.StartBit;
        length = s.Length;
        intel = s.ByteOrder == 1;
        type = s.ValueType;
        ValueTable = s.ValueTableMap is { Count: > 0 } map ? map : null;
        string mux = s.Multiplexing ?? string.Empty;
        IsMultiplexor = mux == "M";
        if (mux.Length > 1 && mux[0] == 'm' && int.TryParse(mux.AsSpan(1).TrimEnd('M'), out int g)) MultiplexGroup = g;
    }

    /// <summary>The raw (unscaled) value, sign-extended for signed signals.</summary>
    public bool TryRaw(ReadOnlySpan<byte> data, out long raw)
    {
        raw = 0;
        if (length is <= 0 or > 64 || data.Length == 0) return false;
        Span<byte> buf = stackalloc byte[8];
        buf.Clear();
        data[..Math.Min(8, data.Length)].CopyTo(buf);
        ulong bits;
        if (intel)
        {
            if (start + length > data.Length * 8) return false;
            ulong le = BinaryPrimitives.ReadUInt64LittleEndian(buf);
            bits = le >> start;
        }
        else
        {
            // Motorola: the start bit is the most significant bit, numbered 7..0 within each byte.
            int msb = start / 8 * 8 + (7 - start % 8);
            if (msb + length > data.Length * 8) return false;
            ulong be = BinaryPrimitives.ReadUInt64BigEndian(buf);
            bits = be >> (64 - msb - length);
        }
        if (length < 64) bits &= (1UL << length) - 1;
        raw = (long)bits;
        if (type == DbcValueType.Signed && length < 64 && (bits & (1UL << (length - 1))) != 0)
            raw = (long)(bits | ~((1UL << length) - 1));
        return true;
    }

    public bool TryDecode(ReadOnlySpan<byte> data, out double value)
    {
        value = double.NaN;
        if (!TryRaw(data, out long raw)) return false;
        double v = type switch
        {
            DbcValueType.IEEEFloat when length == 32 => BitConverter.Int32BitsToSingle((int)raw),
            DbcValueType.IEEEDouble when length == 64 => BitConverter.Int64BitsToDouble(raw),
            DbcValueType.Unsigned => (ulong)raw,
            _ => raw,
        };
        value = v * Factor + Offset;
        return true;
    }

    /// <summary>The value-table text for a decoded value, if the signal has one.</summary>
    public string? TextOf(double physical)
    {
        if (ValueTable == null) return null;
        double raw = Factor != 0 ? (physical - Offset) / Factor : physical;
        return ValueTable.TryGetValue((int)Math.Round(raw), out var text) ? text : null;
    }
}
