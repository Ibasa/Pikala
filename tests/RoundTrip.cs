using System.IO;

namespace Ibasa.Pikala.Tests
{
    public static class RoundTrip
    {
        public static T Do<T>(Pickler pickler, T obj)
        {
            var memoryStream = new MemoryStream();
            pickler.Serialize(memoryStream, obj);

            memoryStream.Position = 0;
            return (T)pickler.Deserialize(memoryStream);
        }

        public static void Assert(Pickler pickler, object obj)
        {
            var result = Do(pickler, obj);
            Xunit.Assert.Equal(obj, result);
        }
    }
}
