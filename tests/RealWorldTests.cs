using FsCheck;
using FsCheck.Xunit;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Ibasa.Pikala.Tests
{
    public class RealWorldTests
    {
        public Pickler CreatePickler()
        {
            var assemblyPickleMode = new Func<System.Reflection.Assembly, AssemblyPickleMode>(assembly => {
                // Don't pikle standard repos like pikla and xunit
                if (assembly == typeof(IReducer).Assembly ||
                    assembly == typeof(FactAttribute).Assembly)
                {
                    return AssemblyPickleMode.PickleByReference;
                }


                return AssemblyPickleMode.PickleByValue;
            });

            var assemblyLoadContext = new System.Runtime.Loader.AssemblyLoadContext("RealWorldTests", true);
            return new Pickler(assemblyPickleMode, assemblyLoadContext);
        }

        /// <summary>
        /// Test for https://github.com/pulumi/pulumi-dotnet/issues/63#issuecomment-1982124649
        /// </summary>
        [Fact(Skip = "Failing now we pickle by value")]
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

        /// <summary>
        /// Test for https://github.com/pulumi/pulumi-dotnet/issues/63#issuecomment-1982124649
        /// </summary>
        [Fact(Skip = "Failing now we pickle by value")]
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