using System;
using System.IO;

namespace Ibasa.Pikala
{
    /// <summary>
    /// Methods for BinaryWriter and BinaryReader for writing some non-standard formats.
    /// </summary>
    static class BinaryExtensions
    {
        public static long Read15BitEncodedLong(this BinaryReader self)
        {
            ulong result = 0;
            ushort shortReadJustNow;
            const int MaxShortsWithoutOverflow = 4;
            for (int shift = 0; shift < MaxShortsWithoutOverflow * 15; shift += 15)
            {
                shortReadJustNow = self.ReadUInt16();
                result |= (shortReadJustNow & 0x7FFFul) << shift;

                if (shortReadJustNow <= 0x7FFFul)
                {
                    return (long)result;
                }
            }
            shortReadJustNow = self.ReadUInt16();
            if (shortReadJustNow > 0b_1111ul)
            {
                throw new FormatException("Too many bytes in what should have been a 15-bit encoded integer.");
            }

            result |= (ulong)shortReadJustNow << (MaxShortsWithoutOverflow * 15);
            return (long)result;
        }

        public static void Write15BitEncodedLong(this BinaryWriter self, long value)
        {
            ulong v = (ulong)value;
            while (v > 0x7FFFu)
            {
                self.Write((ushort)(v | ~0x7FFFu));
                v >>= 15;
            }
            self.Write((ushort)v);
        }

        public static void WriteNullableString(this BinaryWriter self, string? value)
        {
            if (value == null)
            {
                self.Write7BitEncodedInt(-1);
            }
            else
            {
                self.Write7BitEncodedInt(value.Length);
                self.Write(value.AsSpan());
            }
        }

        public static string? ReadNullableString(this BinaryReader self)
        {
            var length = self.Read7BitEncodedInt();
            if (length == -1)
            {
                return null;
            }
            else
            {
                return new string(self.ReadChars(length));
            }
        }
    }
}
