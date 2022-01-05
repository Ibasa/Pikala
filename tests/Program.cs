namespace Ibasa.Pikala.Tests
{
    internal class Program
    {
        /// <summary>
        /// This in place so we can easily run the Visual Studio performance profiler on Pikala tests.
        /// </summary>
        public static void Main(string[] args)
        {
            var tests = new LargeTests();
            tests.Test2GBComplexArray();
        }
    }
}
