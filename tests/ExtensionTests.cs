using FsCheck;
using FsCheck.Xunit;
using System.IO;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    public class ExtensionTests
    {
        [Property]
        public Property Test7Bit()
        {
            return Prop.ForAll(
                Arb.From<int>(),
                value =>
                {
                    var memoryStream = new MemoryStream();
                    var writer = new BinaryWriter(memoryStream);
                    writer.Write7BitEncodedInt(value);
                    memoryStream.Position = 0;
                    var reader = new BinaryReader(memoryStream);
                    var result = reader.Read7BitEncodedInt();
                    Assert.Equal(value, result);
                });
        }

        [Property]
        public Property Test15Bit()
        {
            return Prop.ForAll(
                Arb.From<long>(),
                value =>
                {
                    var memoryStream = new MemoryStream();
                    var writer = new BinaryWriter(memoryStream);
                    writer.Write15BitEncodedLong(value);
                    memoryStream.Position = 0;
                    var reader = new BinaryReader(memoryStream);
                    var result = reader.Read15BitEncodedLong();
                    Assert.Equal(value, result);
                });
        }
    }
}