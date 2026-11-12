using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// Little-endian writer over a caller-owned byte[]. A struct, so it never allocates; pass it by ref.
    /// Writing past the limit never throws: the write is skipped and <see cref="Overflow"/> latches true,
    /// so callers check once at the end.
    /// </summary>
    public struct NetWriter
    {
        byte[] m_Buffer;
        int m_Position;
        int m_Limit;
        bool m_Overflow;

        public NetWriter(byte[] buffer) : this(buffer, 0, buffer != null ? buffer.Length : 0) { }

        /// <param name="limit">Exclusive end index; clamped to the buffer.</param>
        public NetWriter(byte[] buffer, int offset, int limit)
        {
            m_Buffer = buffer;
            int size = buffer != null ? buffer.Length : 0;
            if (limit > size) limit = size;
            if (offset < 0) offset = 0;
            m_Overflow = offset > limit;
            if (m_Overflow) offset = limit < 0 ? 0 : limit;
            m_Position = offset;
            m_Limit = limit;
        }

        public byte[] Buffer => m_Buffer;
        /// <summary>Index of the next byte to be written (also the length when the writer started at 0).</summary>
        public int Position => m_Position;
        public int Remaining => m_Limit - m_Position;
        public bool Overflow => m_Overflow;

        bool Reserve(int count)
        {
            if (m_Overflow || m_Buffer == null || count < 0 || m_Position + count > m_Limit)
            {
                m_Overflow = true;
                return false;
            }
            return true;
        }

        public void U8(byte value)
        {
            if (!Reserve(1)) return;
            m_Buffer[m_Position++] = value;
        }

        public void Bool(bool value) { U8(value ? (byte)1 : (byte)0); }

        public void U16(ushort value)
        {
            if (!Reserve(2)) return;
            m_Buffer[m_Position++] = (byte)value;
            m_Buffer[m_Position++] = (byte)(value >> 8);
        }

        public void I16(short value) { U16((ushort)value); }

        public void U32(uint value)
        {
            if (!Reserve(4)) return;
            m_Buffer[m_Position++] = (byte)value;
            m_Buffer[m_Position++] = (byte)(value >> 8);
            m_Buffer[m_Position++] = (byte)(value >> 16);
            m_Buffer[m_Position++] = (byte)(value >> 24);
        }

        public void I32(int value) { U32((uint)value); }

        public void F32(float value) { U32((uint)BitConverter.SingleToInt32Bits(value)); }

        public void Bytes(byte[] source, int offset, int count)
        {
            if (count == 0) return;
            if (source == null || offset < 0 || count < 0 || offset + count > source.Length) { m_Overflow = true; return; }
            if (!Reserve(count)) return;
            System.Buffer.BlockCopy(source, offset, m_Buffer, m_Position, count);
            m_Position += count;
        }

        /// <summary>Overwrites two bytes written earlier (used for length prefixes). Out of range is ignored.</summary>
        public void PatchU16(int at, ushort value)
        {
            if (m_Buffer == null || at < 0 || at + 2 > m_Position) { m_Overflow = true; return; }
            m_Buffer[at] = (byte)value;
            m_Buffer[at + 1] = (byte)(value >> 8);
        }

        /// <summary>Moves the write position back to an earlier point (drops a partly written message).</summary>
        public void Rewind(int position)
        {
            if (position < 0 || position > m_Position) return;
            m_Position = position;
            m_Overflow = false;
        }
    }

    /// <summary>
    /// Little-endian, bounds-checked reader over a caller-owned byte[]. A struct; pass it by ref.
    /// Reading past the end never throws: it returns 0 and <see cref="Failed"/> latches true. Every message
    /// reader ends with "return !r.Failed", which is how truncated datagrams get dropped.
    /// </summary>
    public struct NetReader
    {
        byte[] m_Buffer;
        int m_Position;
        int m_End;
        bool m_Failed;

        public NetReader(byte[] buffer, int offset, int length)
        {
            m_Buffer = buffer;
            int size = buffer != null ? buffer.Length : 0;
            m_Failed = buffer == null || offset < 0 || length < 0 || offset > size || length > size - offset;
            if (m_Failed) { offset = 0; length = 0; }
            m_Position = offset;
            m_End = offset + length;
        }

        public byte[] Buffer => m_Buffer;
        public int Position => m_Position;
        public int Remaining => m_End - m_Position;
        public bool Failed => m_Failed;
        public bool AtEnd => m_Position >= m_End;

        bool Take(int count)
        {
            if (m_Failed || count < 0 || m_Position + count > m_End)
            {
                m_Failed = true;
                return false;
            }
            return true;
        }

        public byte U8()
        {
            if (!Take(1)) return 0;
            return m_Buffer[m_Position++];
        }

        public bool Bool() { return U8() != 0; }

        public ushort U16()
        {
            if (!Take(2)) return 0;
            int v = m_Buffer[m_Position] | (m_Buffer[m_Position + 1] << 8);
            m_Position += 2;
            return (ushort)v;
        }

        public short I16() { return (short)U16(); }

        public uint U32()
        {
            if (!Take(4)) return 0;
            uint v = (uint)(m_Buffer[m_Position] | (m_Buffer[m_Position + 1] << 8) | (m_Buffer[m_Position + 2] << 16)) | ((uint)m_Buffer[m_Position + 3] << 24);
            m_Position += 4;
            return v;
        }

        public int I32() { return (int)U32(); }

        /// <summary>Reads a float. NaN and infinity count as a failed read (they would poison poses and timers).</summary>
        public float F32()
        {
            float f = BitConverter.Int32BitsToSingle((int)U32());
            if (float.IsNaN(f) || float.IsInfinity(f)) { m_Failed = true; return 0f; }
            return f;
        }

        public void Bytes(byte[] destination, int offset, int count)
        {
            if (count == 0) return;
            if (destination == null || offset < 0 || count < 0 || offset + count > destination.Length) { m_Failed = true; return; }
            if (!Take(count)) return;
            System.Buffer.BlockCopy(m_Buffer, m_Position, destination, offset, count);
            m_Position += count;
        }

        public void Skip(int count)
        {
            if (!Take(count)) return;
            m_Position += count;
        }

        /// <summary>Marks the read as failed (a field held a value that is out of range).</summary>
        public void Fail() { m_Failed = true; }
    }
}
