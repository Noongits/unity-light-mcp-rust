using UnityEngine;
using Object = UnityEngine.Object;
namespace MCPForUnityTests.Editor.Tools
{
    public class ResourceBatchThreeHolder : ScriptableObject
    {
        public Object[] references;
        public SampleMode mode;
        public enum SampleMode { First, Second }
    }

}
