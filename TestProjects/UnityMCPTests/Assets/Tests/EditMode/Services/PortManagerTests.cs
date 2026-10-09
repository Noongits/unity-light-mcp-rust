using System.Reflection;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnityTests.Editor.Services
{
    [TestFixture]
    public class PortManagerTests
    {
        // Availability tests must not call DiscoverNewPort: it persists into the user's
        // real registry. Persistence needs a separate isolated-storage integration test.
        private static int FindAvailablePort()
        {
            var method = typeof(PortManager).GetMethod("FindAvailablePort",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            return (int)method.Invoke(null, null);
        }

        [Test]
        public void IsPortAvailable_ReturnsFalse_WhenPortIsOccupied()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            try
            {
                Assert.IsFalse(PortManager.IsPortAvailable(port),
                    "IsPortAvailable should return false for a port that is already bound");
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public void IsPortAvailable_ReturnsTrue_WhenPortIsFree()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            Assert.IsTrue(PortManager.IsPortAvailable(port),
                "IsPortAvailable should return true for a port that is not bound");
        }

#if UNITY_EDITOR_OSX
        [Test]
        public void IsPortAvailable_ReturnsFalse_WhenPortHeldWithReuseAddr()
        {
            // Simulate what AssetImportWorkers do: bind with SO_REUSEADDR.
            // IsPortAvailable must still detect this as occupied.
            var holder = new TcpListener(IPAddress.Loopback, 0);
            holder.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            holder.Start();
            int port = ((IPEndPoint)holder.LocalEndpoint).Port;

            try
            {
                Assert.IsFalse(PortManager.IsPortAvailable(port),
                    "IsPortAvailable should detect ports held with SO_REUSEADDR on macOS");
            }
            finally
            {
                holder.Stop();
            }
        }
#endif

        [Test]
        public void ShouldAbandonBusyPort_KeepsSamePort_WithinReleaseWindow()
        {
            // A port busy for less than the fallback window is treated as our own
            // not-yet-released listener after a domain reload — keep retrying the same
            // port instead of silently switching and stranding the client (#1173).
            Assert.IsFalse(PortManager.ShouldAbandonBusyPort(0.0));
            Assert.IsFalse(PortManager.ShouldAbandonBusyPort(
                PortManager.BusyPortFallbackWindowSeconds - 0.5));
        }

        [Test]
        public void ShouldAbandonBusyPort_FallsBack_AfterReleaseWindow()
        {
            // A port that stays busy past the window is a foreign occupant — only then
            // does the bridge discover and switch to a new port.
            Assert.IsTrue(PortManager.ShouldAbandonBusyPort(
                PortManager.BusyPortFallbackWindowSeconds));
            Assert.IsTrue(PortManager.ShouldAbandonBusyPort(
                PortManager.BusyPortFallbackWindowSeconds + 5.0));
        }

        [Test]
        public void FindAvailablePort_ReturnsAvailablePort()
        {
            int port = FindAvailablePort();
            Assert.Greater(port, 0, "FindAvailablePort should return a positive port number");
            Assert.IsTrue(PortManager.IsPortAvailable(port),
                "The port returned by FindAvailablePort should be available");
        }

        [Test]
        public void FindAvailablePort_SkipsOccupiedDefaultPort()
        {
            // Hold the default port (6400) so FindAvailablePort must find an alternative
            TcpListener holder = null;
            try
            {
                holder = new TcpListener(IPAddress.Loopback, 6400);
#if UNITY_EDITOR_OSX
                try { holder.Server.ExclusiveAddressUse = true; } catch { }
#endif
                holder.Start();
            }
            catch (SocketException)
            {
                // Port 6400 already occupied (e.g., by the running bridge) — that's fine,
                // the test still validates that FindAvailablePort picks a different port.
                holder?.Stop();
                holder = null;
            }

            try
            {
                int port = FindAvailablePort();
                Assert.AreNotEqual(6400, port,
                    "FindAvailablePort should not return the default port when it is occupied");
                Assert.Greater(port, 0);
            }
            finally
            {
                holder?.Stop();
            }
        }
    }
}
