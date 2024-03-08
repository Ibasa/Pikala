using FsCheck;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    public class RealWorldTests
    {
        public Pickler CreatePickler()
        {
            var assemblyPickleMode = new Func<System.Reflection.Assembly, AssemblyPickleMode>(assembly =>
            {
                // Don't pikle standard repos like pikla and xunit
                if (assembly == typeof(IReducer).Assembly ||
                    assembly == typeof(FactAttribute).Assembly ||
                    assembly == typeof(Assert).Assembly)
                {
                    return AssemblyPickleMode.PickleByReference;
                }


                return AssemblyPickleMode.PickleByValue;
            });

            var assemblyLoadContext = new System.Runtime.Loader.AssemblyLoadContext("RealWorldTests", true);
            return new Pickler(assemblyPickleMode, assemblyLoadContext);
        }

        /*
        [Fact]
        public void TestRSACryptoServiceProvider()
        {
            var pickler = CreatePickler();
            var rsaFunc = () =>
            {
                var csp = new System.Security.Cryptography.RSACryptoServiceProvider(1024);
                var parameters = csp.ExportParameters(includePrivateParameters: true);
                var memory = new System.IO.MemoryStream();
                memory.Write(parameters.Modulus);
                memory.Write(parameters.Exponent);
                memory.Write(parameters.D);
                memory.Write(parameters.P);
                memory.Write(parameters.Q);
                memory.Write(parameters.DP);
                memory.Write(parameters.DQ);
                memory.Write(parameters.InverseQ);
                return System.Convert.ToBase64String(memory.ToArray());
            };

            var result = RoundTrip.Do(pickler, rsaFunc);

            var str = result();
            Assert.NotNull(str);
            Assert.Equal(772, str.Length);
        }
        */

        /// <summary>
        /// Test for https://github.com/pulumi/pulumi-dotnet/issues/63#issuecomment-1982124649
        /// </summary>
        /*
        [Fact]
        public void TestSshKeyGeneration()
        {
            var pickler = CreatePickler();
            var sshFunc = () =>
            {
                var keygen = new SshKeyGenerator.SshKeyGenerator(2048);

                var privateKey = keygen.ToPrivateKey();
                var publicKey = keygen.ToRfcPublicKey();

                return privateKey + ":" + publicKey;
            };

            var result = RoundTrip.Do(pickler, sshFunc);

            var keys = result();
            var split = keys.Split(':');
            Assert.Equal(2, split.Length);
            Assert.NotEmpty(split[0]);
            Assert.NotEmpty(split[1]);
        }
        */

        /// <summary>
        /// Test for https://github.com/pulumi/pulumi-dotnet/issues/63#issuecomment-1982124649
        /// </summary>
        [Fact]
        public void TestTask()
        {
            var pickler = CreatePickler();
            var taskFunc = () =>
            {
                return Task.FromResult(123);
            };

            var result = RoundTrip.Do(pickler, taskFunc);

            var task = result();
            Assert.NotNull(task);
            var number = task.Result;
            Assert.Equal(123, number);
        }
    }
}