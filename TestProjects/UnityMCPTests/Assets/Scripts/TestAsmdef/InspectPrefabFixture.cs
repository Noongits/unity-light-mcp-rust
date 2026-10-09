using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace TestNamespace
{
    /// <summary>Fixture for inspect_prefab tests: values, references, a list and a UnityEvent.</summary>
    public class InspectPrefabFixture : MonoBehaviour
    {
        public int value = 5;
        public float speed = 1f;
        public string label = "default";
        public GameObject target;
        public Transform other;
        public Material mat;
        public List<int> numbers = new List<int>();
        public UnityEvent onClick = new UnityEvent();
        public UnityEvent<int> onValue = new UnityEvent<int>();

        public void Ping() { }

        public void SetValue(int newValue) { value = newValue; }
    }
}
