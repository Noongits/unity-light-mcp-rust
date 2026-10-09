using System;
using System.Collections.Generic;
using System.IO;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport.Transports;
using UnityEditor;
using UnityEngine;
[InitializeOnLoad]
public static class ReviewBridge
{
    const string Active="IndependentReview.LiveBridge";
    static ReviewBridge()
    {
        if(SessionState.GetBool(Active,false))EditorApplication.delayCall+=Boot;
    }
    public static void Run()
    {
        SessionState.SetBool(Active,true);
        Boot();
    }
    private sealed class Tools : IToolDiscoveryService
    {
        readonly IToolDiscoveryService original;
        public Tools(IToolDiscoveryService original){this.original=original;}
        public List<ToolMetadata> DiscoverAllTools()=>original.DiscoverAllTools();
        public ToolMetadata GetToolMetadata(string name)=>original.GetToolMetadata(name);
        public List<ToolMetadata> GetEnabledTools()=>original.DiscoverAllTools();
        public bool IsToolEnabled(string name)=>true;
        public void SetToolEnabled(string name,bool enabled){}
        public void InvalidateCache()=>original.InvalidateCache();
    }
    private static void Boot()
    {
        if(EditorApplication.isCompiling||EditorApplication.isUpdating){EditorApplication.delayCall+=Boot;return;}
        EditorConfigurationCache.Instance.PinStdioForSession();
        MCPServiceLocator.Register<IToolDiscoveryService>(new Tools(MCPServiceLocator.ToolDiscovery));
        if(!StdioBridgeHost.IsRunning)
        {
            if (!SessionState.GetBool(Active+".Configured",false))
            {
                PortManager.SetPreferredPort(int.Parse(Environment.GetEnvironmentVariable("REVIEW_BRIDGE_PORT")));
                SessionState.SetBool(Active+".Configured",true);
            }
            StdioBridgeHost.Start();
        }
        File.WriteAllText(Environment.GetEnvironmentVariable("REVIEW_BRIDGE_READY"),StdioBridgeHost.GetCurrentPort().ToString());
        EditorApplication.update-=CheckStop;EditorApplication.update+=CheckStop;
    }
    private static void CheckStop()
    {
        File.WriteAllText(Environment.GetEnvironmentVariable("REVIEW_BRIDGE_READY"),StdioBridgeHost.GetCurrentPort().ToString());
        if(!File.Exists(Environment.GetEnvironmentVariable("REVIEW_BRIDGE_STOP")))return;
        SessionState.SetBool(Active,false);StdioBridgeHost.Stop();EditorApplication.Exit(0);
    }
}
