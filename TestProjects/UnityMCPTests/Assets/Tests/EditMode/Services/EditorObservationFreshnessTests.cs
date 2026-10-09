using System.Collections.Generic;
using System.Reflection;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public class EditorObservationFreshnessTests
    {
        [Test]
        public void UnchangedEditorTick_RefreshesObservationWithoutRebuildingBody()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var type = typeof(EditorStateCache);
            var saved = new Dictionary<FieldInfo, object>();
            foreach (var field in type.GetFields(flags))
                if (!field.IsLiteral && !field.IsInitOnly) saved[field] = field.GetValue(null);
            try
            {
                var update = type.GetMethod("OnUpdate", flags);
                var lastTick = type.GetField("_lastUpdateTimeSinceStartup", flags);
                lastTick.SetValue(null, double.NegativeInfinity);
                update.Invoke(null, null); // synchronize tracked values
                var cachedField = type.GetField("_cached", flags);
                var body = (JObject)cachedField.GetValue(null);
                // Work on a clone so restoring saved fields also restores the old data.
                body = (JObject)body.DeepClone();
                cachedField.SetValue(null, body);
                body["observed_at_unix_ms"] = 1L;
                long sequence = body.Value<long>("sequence");
                var activity = body["activity"];
                lastTick.SetValue(null, double.NegativeInfinity);
                update.Invoke(null, null);
                Assert.AreSame(body, cachedField.GetValue(null), "idle ticks must retain the expensive body");
                Assert.Greater(body.Value<long>("observed_at_unix_ms"), 1L);
                Assert.Greater(body.Value<long>("sequence"), sequence);
                Assert.AreSame(activity, body["activity"], "observing idle must not restart its activity age");
                Assert.AreNotEqual(double.NegativeInfinity, lastTick.GetValue(null), "unchanged ticks must also be throttled");
            }
            finally
            {
                foreach (var pair in saved) pair.Key.SetValue(null, pair.Value);
            }
        }
    }
}
