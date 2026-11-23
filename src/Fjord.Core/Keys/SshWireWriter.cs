using System.Buffers.Binary;
using System.Text;

namespace Fjord.Core.Keys;

/// <summary>Writes the SSH wire encoding (RFC 4251 §5): uint32, string, mpint.</summary>
internal sealed class SshWireWriter
{
    private readonly MemoryStream _ms = new();

    public SshWireWriter UInt32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        _ms.Write(b);
        return this;
    }

    public SshWireWriter String(ReadOnlySpan<byte> value)
    {
        UInt32((uint)value.Length);
        _ms.Write(value);
        return this;
    }

    public SshWireWriter String(string value) => String(Encoding.UTF8.GetBytes(value));

    /// <summary>Unsigned big-endian magnitude → SSH mpint (two's complement, minimal length).</summary>
    public SshWireWriter MPInt(ReadOnlySpan<byte> unsignedBigEndian)
    {
        int i = 0;
        while (i < unsignedBigEndian.Length && unsignedBigEndian[i] == 0) i++;
        var magnitude = unsignedBigEndian[i..];
        if (magnitude.Length == 0) return UInt32(0);

        bool needsPad = (magnitude[0] & 0x80) != 0;
        UInt32((uint)(magnitude.Length + (needsPad ? 1 : 0)));
        if (needsPad) _ms.WriteByte(0);
        _ms.Write(magnitude);
        return this;
    }

    public SshWireWriter Raw(ReadOnlySpan<byte> bytes)
    {
        _ms.Write(bytes);
        return this;
    }

    public int Length => (int)_ms.Length;

    public byte[] ToArray() => _ms.ToArray();
}
