using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
public static class ReviewDiscovery
{
    static TestRunnerApi api;
    public static void Run()
    {
        api = ScriptableObject.CreateInstance<TestRunnerApi>();
        var cases = new JArray();
        api.RetrieveTestList(Environment.GetEnvironmentVariable("REVIEW_DISCOVERY_MODE") == "PlayMode" ? TestMode.PlayMode : TestMode.EditMode, tree =>
        {
            Walk(tree, cases, false);
            File.WriteAllText(Environment.GetEnvironmentVariable("REVIEW_DISCOVERY_OUTPUT"), cases.ToString());
            Debug.Log("Independent discovery: " + cases.Count);
            UnityEngine.Object.DestroyImmediate(api);
            EditorApplication.Exit(0);
        });
    }
    static void Walk(ITestAdaptor node, JArray result, bool explicitParent)
    {
        bool excluded = explicitParent || node.RunState.ToString() == "Explicit";
        if (!node.IsSuite) result.Add(new JObject { ["name"] = node.FullName, ["runState"] = node.RunState.ToString(), ["explicit"] = excluded, ["skipReason"] = node.SkipReason });
        foreach (var child in node.Children) Walk(child, result, excluded);
    }
}
