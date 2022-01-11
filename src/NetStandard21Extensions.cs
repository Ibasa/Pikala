using System;
using System.IO;

namespace Ibasa.Pikala
{
    /// <summary>
    /// Couple of methods that are available in net5.0 but not in netstandard2.1.
    /// </summary>
    static class NetStandard21Extensions
    {
        public static int Read7BitEncodedInt(this BinaryReader self)
        {
            uint result = 0;
            byte byteReadJustNow;
            const int MaxBytesWithoutOverflow = 4;
            for (int shift = 0; shift < MaxBytesWithoutOverflow * 7; shift += 7)
            {
                byteReadJustNow = self.ReadByte();
                result |= (byteReadJustNow & 0x7Fu) << shift;

                if (byteReadJustNow <= 0x7Fu)
                {
                    return (int)result;
                }
            }
            byteReadJustNow = self.ReadByte();
            if (byteReadJustNow > 0b_1111u)
            {
                throw new FormatException("Too many bytes in what should have been a 7-bit encoded integer.");
            }

            result |= (uint)byteReadJustNow << (MaxBytesWithoutOverflow * 7);
            return (int)result;
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

        public static long Read15BitEncodedLong(this BinaryReader self)
        {
            ulong result = 0;
            ushort bytesReadJustNow;
            const int MaxBytesWithoutOverflow = 8;
            for (int shift = 0; shift < MaxBytesWithoutOverflow * 15; shift += 15)
            {
                bytesReadJustNow = self.ReadUInt16();
                result |= (bytesReadJustNow & 0x7FFFu) << shift;

                if (bytesReadJustNow <= 0x7FFFu)
                {
                    return (long)result;
                }
            }
            bytesReadJustNow = self.ReadUInt16();
            if (bytesReadJustNow > 0b_1111u)
            {
                throw new FormatException("Too many bytes in what should have been a 7-bit encoded integer.");
            }

            result |= (ulong)bytesReadJustNow << (MaxBytesWithoutOverflow * 15);
            return (long)result;
        }

        public static void Write7BitEncodedInt(this BinaryWriter self, int value)
        {
            uint v = (uint)value;
            while (v > 0x7Fu)
            {
                self.Write((byte)(v | ~0x7Fu));
                v >>= 7;
            }
            self.Write((byte)v);
        }

        public static bool IsAssignableTo(this Type self, Type type)
        {
            return type.IsAssignableFrom(self);
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
