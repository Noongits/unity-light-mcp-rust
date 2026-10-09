using System;
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
[InitializeOnLoad]
public static class ReviewTrace
{
    static ReviewTrace()
    {
        var path=Environment.GetEnvironmentVariable("REVIEW_TRACE");
        if (!string.IsNullOrEmpty(path)) TestRunnerApi.RegisterTestCallback(new Trace(path));
    }
    private sealed class Trace : ICallbacks
    {
        readonly string path;
        public Trace(string path){this.path=path;}
        public void RunStarted(ITestAdaptor test)=>Write("run",test.FullName);
        public void RunFinished(ITestResultAdaptor result)=>Write("runFinished",result.ResultState);
        public void TestStarted(ITestAdaptor test){if(!test.IsSuite)Write("start",test.FullName);}
        public void TestFinished(ITestResultAdaptor result){if(!result.Test.IsSuite)Write("finish",result.Test.FullName+" "+result.ResultState);}
        void Write(string state,string value)=>File.AppendAllText(path,DateTime.UtcNow.ToString("O")+" "+state+" "+value+"\n");
    }
}
