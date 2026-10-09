using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Security;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.AssetGen
{
    [TestFixture]
    public class KeyMaterialTimelineSafetyTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "mcp_key_timeline_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [TestCase("secret.bin")]
        [TestCase("salt.bin")]
        public void ExistingStore_CorruptMaterial_IsNotReplacedByReadOrWrite(string name)
        {
            var store = new EncryptedFileKeyStore(_directory);
            store.Set("tripo", "synthetic-test-key-one");
            string materialPath = Path.Combine(_directory, name);
            byte[] originalMaterial = File.ReadAllBytes(materialPath);
            byte[] damaged = { 1, 2, 3 };
            File.WriteAllBytes(materialPath, damaged);

            Assert.IsFalse(store.TryGet("tripo", out _));
            Assert.Throws<CryptographicException>(() => store.Set("meshy", "synthetic-test-key-two"));
            CollectionAssert.AreEqual(damaged, File.ReadAllBytes(materialPath));
            Assert.IsFalse(File.Exists(Path.Combine(_directory, "key_meshy.bin")));

            File.WriteAllBytes(materialPath, originalMaterial);
            Assert.IsTrue(store.TryGet("tripo", out string value));
            Assert.AreEqual("synthetic-test-key-one", value);
        }

        [TestCase("secret.bin")]
        [TestCase("salt.bin")]
        public void ExistingStore_MissingMaterial_ReadAndWriteDoNotCreateReplacement(string name)
        {
            var store = new EncryptedFileKeyStore(_directory);
            store.Set("tripo", "synthetic-test-key");
            string materialPath = Path.Combine(_directory, name);
            File.Delete(materialPath);

            Assert.IsFalse(store.TryGet("tripo", out _));
            Assert.Throws<CryptographicException>(() => store.Set("meshy", "synthetic-new-key"));
            Assert.IsFalse(File.Exists(materialPath));
        }

        [Test]
        public void InitializersCompete_AllUseSameCompleteMaterialAndCleanTemporaryFiles()
        {
            for (int iteration = 0; iteration < 16; ++iteration)
            {
            string path = Path.Combine(_directory, "competing_" + iteration + ".bin");
            using (var ready = new ManualResetEventSlim(false))
            {
                var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                {
                    ready.Wait();
                    return EncryptedFileKeyStore.LoadKeyMaterial(path, 32, true);
                })).ToArray();
                ready.Set();
                Assert.IsTrue(Task.WaitAll(workers, 10000), "Initializers should complete without deadlock");
                byte[] saved = File.ReadAllBytes(path);
                Assert.AreEqual(32, saved.Length);
                foreach (var worker in workers) CollectionAssert.AreEqual(saved, worker.Result);
            }
            Assert.IsEmpty(Directory.GetFiles(_directory, "*.tmp"));
            }
        }

        [Test]
        public void MissingMaterial_ReadOnlyLookup_DoesNotInitializeStore()
        {
            string path = Path.Combine(_directory, "secret.bin");
            Assert.Throws<CryptographicException>(() => EncryptedFileKeyStore.LoadKeyMaterial(path, 32, false));
            Assert.IsEmpty(Directory.GetFiles(_directory));
        }
    }
}
