using FsCheck;
using FsCheck.Xunit;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    public class RealWorldTests
    {
        public Pickler CreatePickler()
        {
            var assemblyPickleMode = new Func<System.Reflection.Assembly, AssemblyPickleMode>(assembly =>
                assembly == System.Reflection.Assembly.GetExecutingAssembly() ? AssemblyPickleMode.PickleByValue : AssemblyPickleMode.Default
            );

            var assemblyLoadContext = new System.Runtime.Loader.AssemblyLoadContext("RealWorldTests", true);
            return new Pickler(assemblyPickleMode, assemblyLoadContext);
        }

        /// <summary>
        /// Test for https://github.com/pulumi/pulumi-dotnet/issues/63#issuecomment-1982124649
        /// </summary>
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
    }
}