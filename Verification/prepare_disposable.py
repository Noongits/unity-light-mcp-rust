"""Create a fresh, separately owned Unity review project. Never overwrite a project."""
import argparse,json,pathlib,shutil,urllib.request,zipfile,io,ssl
p=argparse.ArgumentParser();p.add_argument('destination');p.add_argument('--with-roslyn',action='store_true');a=p.parse_args()
root=pathlib.Path(__file__).resolve().parents[1];dest=pathlib.Path(a.destination).resolve()
if dest.exists():raise SystemExit('Destination exists; choose a new disposable directory.')
source=root/'TestProjects/UnityMCPTests'
shutil.copytree(source/'Assets',dest/'Assets');shutil.copytree(source/'ProjectSettings',dest/'ProjectSettings');(dest/'Packages').mkdir()
deps={'com.coplaydev.unity-mcp':'file:'+str(root/'MCPForUnity'),'com.unity.test-framework':'1.6.0','com.unity.ugui':'2.0.0','com.unity.render-pipelines.universal':'17.3.0','com.unity.cinemachine':'3.1.6','com.unity.ai.navigation':'2.0.14','com.unity.timeline':'1.8.13','com.unity.asset-store-tools':'file:'+str(root/'TestProjects/AssetStoreUploads/Packages/com.unity.asset-store-tools')}
(dest/'Packages/manifest.json').write_text(json.dumps({'dependencies':deps},indent=2))
for name in ['ReviewDiscovery.cs','ReviewTrace.cs','ReviewBridge.cs','ReviewValidator.cs']:
 shutil.copy2(root/'Verification'/name,dest/'Assets/Tests/EditMode'/name)
if a.with_roslyn:
 tools=dest/'Assets/IndependentCustomTools';(tools/'Runtime').mkdir(parents=True);(tools/'Editor').mkdir()
 for name,folder in [('RoslynRuntimeCompiler.cs','Runtime'),('ManageRuntimeCompilation.cs','Editor')]:shutil.copy2(root/'CustomTools/RoslynRuntimeCompilation'/name,tools/folder/name)
 asm=json.loads((root/'MCPForUnity/Editor/MCPForUnity.Editor.asmdef').read_text());asm.update(name='IndependentCustomTools.Runtime',references=[],includePlatforms=[])
 (tools/'Runtime/IndependentCustomTools.Runtime.asmdef').write_text(json.dumps(asm,indent=2))
 asm.update(name='IndependentCustomTools.Editor',references=['MCPForUnity.Editor','IndependentCustomTools.Runtime'],includePlatforms=['Editor'])
 (tools/'Editor/IndependentCustomTools.Editor.asmdef').write_text(json.dumps(asm,indent=2))
 folder=dest/'Assets/Plugins/Roslyn';folder.mkdir(parents=True)
 try:
  import certifi
  ctx=ssl.create_default_context(cafile=certifi.where())
 except ImportError:ctx=ssl.create_default_context()
 for name,version,dll in [('microsoft.codeanalysis.common','4.12.0','Microsoft.CodeAnalysis'),('microsoft.codeanalysis.csharp','4.12.0','Microsoft.CodeAnalysis.CSharp'),('system.collections.immutable','8.0.0','System.Collections.Immutable'),('system.reflection.metadata','8.0.0','System.Reflection.Metadata'),('system.runtime.compilerservices.unsafe','6.0.0','System.Runtime.CompilerServices.Unsafe')]:
  url=f'https://api.nuget.org/v3-flatcontainer/{name}/{version}/{name}.{version}.nupkg'
  data=urllib.request.urlopen(url,context=ctx,timeout=30).read();(folder/(dll+'.dll')).write_bytes(zipfile.ZipFile(io.BytesIO(data)).read('lib/netstandard2.0/'+dll+'.dll'))
print(dest)
