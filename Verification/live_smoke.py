"""Run only against the disposable ReviewBridge editor; never a default instance."""
import asyncio,base64,json,os,pathlib,time
from mcp import ClientSession,StdioServerParameters
from mcp.client.stdio import stdio_client
ROOT=pathlib.Path(__file__).resolve().parents[1]
OUT=pathlib.Path(os.environ['REVIEW_EVIDENCE']); PROJECT=pathlib.Path('/tmp/unity-mcp-independent-review')
BINARY=ROOT/'ServerRust/target/release/unity-mcp-light'
records=[]
if (OUT/'live-environment.json').exists(): os.environ.update(json.loads((OUT/'live-environment.json').read_text()))
def record(name,value):
    records.append({'step':name,'result':value});(OUT/'live-smoke.json').write_text(json.dumps(records,indent=2));print(name,json.dumps(value)[:1600],flush=True)
def parsed(result):
    raw=result.model_dump(mode='json',by_alias=True)
    for c in raw.get('content',[]):
        if c.get('type')=='image':
            filename=f'live-{len(records)}.png';(OUT/filename).write_bytes(base64.b64decode(c['data']));c['data']=f'<saved {filename}>'
    return raw
async def main():
    params=StdioServerParameters(command=str(BINARY),args=['--transport','stdio','--project-scoped-tools'],env={**os.environ,'UNITY_MCP_STATUS_DIR':os.environ['UNITY_MCP_STATUS_DIR'],'DISABLE_TELEMETRY':'true'})
    async with stdio_client(params) as (read,write):
      async with ClientSession(read,write) as s:
        init=await s.initialize();record('initialize',init.model_dump(mode='json',by_alias=True));assert init.server_info.name=='unity-mcp-light'
        async def call(name,args):
            r=await s.call_tool(name,args);raw=parsed(r);record(name,raw);assert not r.is_error,raw
            data=r.structured_content
            if data is None:
                data=json.loads(next(c.text for c in r.content if c.type=='text'))
            if set(data)=={'result'}: data=data['result']
            assert data.get('success',True),data
            return data
        status_files=list(pathlib.Path(os.environ['UNITY_MCP_STATUS_DIR']).glob('unity-mcp-status-*.json'))
        assert len(status_files)==1,status_files
        status=json.loads(status_files[0].read_text())
        assert 'unity-mcp-independent-review' in status['project_path'],status
        instance=status['project_name']+'@'+status_files[0].stem.removeprefix('unity-mcp-status-')
        await call('set_active_instance',{'instance':instance})
        info=await s.read_resource('mcpforunity://project/info');raw=info.model_dump(mode='json',by_alias=True);record('project/info',raw)
        assert 'unity-mcp-independent-review' in json.dumps(raw),raw
        record('tools/list',{'count':len((await s.list_tools()).tools)})
        record('editor/state',parsed(await s.read_resource('mcpforunity://editor/state')))
        if not os.environ.get('REVIEW_RESUME_SCRIPTS'):
            await call('manage_gameobject',{'action':'create','name':'IndependentCube','primitive_type':'Cube','position':[0,0,3]})
            await call('manage_prefabs',{'action':'create_from_gameobject','target':'IndependentCube','prefab_path':'Assets/IndependentSmoke/Cube.prefab'})
            await call('manage_prefabs',{'action':'get_info','prefab_path':'Assets/IndependentSmoke/Cube.prefab'})
            await call('manage_camera',{'action':'ping'})
            await call('manage_gameobject',{'action':'create','name':'IndependentCamera','components_to_add':['Camera'],'position':[0,0,0]})
            await call('manage_camera',{'action':'screenshot','camera':'IndependentCamera','include_image':True,'max_resolution':128,'output_folder':'Captures/IndependentSmoke'})
            await call('manage_ui',{'action':'create','path':'Assets/IndependentSmoke/Probe.uxml','contents':'<ui:UXML xmlns:ui="UnityEngine.UIElements"><ui:VisualElement style="width:64px;height:64px;background-color:rgb(255,0,0);"/></ui:UXML>'})
            await call('manage_ui',{'action':'render_ui','path':'Assets/IndependentSmoke/Probe.uxml','width':64,'height':64,'include_image':True,'output_folder':'Captures/IndependentSmoke'})
            script='using UnityEngine; public class IndependentSmoke : MonoBehaviour { public int Value(){ return 1; } public int Other(){ return 7; } }'
            await call('create_script',{'path':'Assets/IndependentSmoke/IndependentSmoke.cs','contents':script})
            await ready(s)
        if not os.environ.get('REVIEW_RESUME_RECOVERY'):
            if os.environ.get('REVIEW_RESUME_SCRIPTS'):
                await call('refresh_unity',{'mode':'force','scope':'all','compile':'request','wait_for_ready':False})
                await ready(s)
            await call('script_apply_edits',{'name':'IndependentSmoke','path':'Assets/IndependentSmoke','edits':[{'op':'replace_method','className':'IndependentSmoke','methodName':'Value','replacement':'public int Value(){ return 2; }'}]})
            await ready(s)
        text=(PROJECT/'Assets/IndependentSmoke/IndependentSmoke.cs').read_text();assert 'return 2;' in text and 'return 7;' in text;record('script_boundary_check',{'preservedOtherMethod':True,'contents':text})
        # Deliberate compiler error stays within this disposable project.
        broken=PROJECT/'Assets/IndependentSmoke/DeliberateBroken.cs';broken.write_text('public class DeliberateBroken { public void Broken( }')
        r=await s.call_tool('refresh_unity',{'mode':'force','scope':'all','compile':'request','wait_for_ready':False});record('failed_compile_refresh',parsed(r))
        await asyncio.sleep(10)
        r=await s.call_tool('read_console',{'types':['error'],'count':30,'include_stacktrace':False});record('failed_compile_console',parsed(r));assert 'DeliberateBroken' in json.dumps(parsed(r))
        broken.unlink();broken.with_suffix(broken.suffix+".meta").unlink(missing_ok=True)
        r=await s.call_tool('refresh_unity',{'mode':'force','scope':'all','compile':'request','wait_for_ready':False});record('recovery_refresh',parsed(r));await ready(s)
        job=await call('run_tests',{'mode':'EditMode','test_names':['MCPForUnityTests.Editor.Services.CrossModeLifecycleRegressionTests.AllModeStop_WaitingForHttp_MustNotStopNewerStdioStart'],'include_details':True})
        job_id=job.get('job_id') or job.get('data',{}).get('job_id');assert job_id,job
        for _ in range(20):
            result=await call('get_test_job',{'job_id':job_id,'include_details':True,'wait_timeout':15})
            if result.get('data',result).get('status') in ('completed','complete','finished','succeeded','failed','cancelled'):break
            await asyncio.sleep(1)
        data=result.get('data',result)
        assert data['status']=='succeeded' and data['result']['summary']['passed']==1 and data['result']['summary']['failed']==0,data
        record('completed',{'success':True})
async def ready(s):
    # Only readiness reads are retried across domain reload, never mutations.
    await asyncio.sleep(3)
    for _ in range(50):
        try:
            r=await s.read_resource('mcpforunity://editor/state');raw=r.model_dump(mode='json',by_alias=True)
            text=json.dumps(raw)
            payload=json.loads(raw['contents'][0]['text'])
            data=payload.get('data',payload)
            if data.get('advice',{}).get('ready_for_tools') is True:
                record('ready_after_reload',raw);return
        except Exception as e:
            record('reload_read_retry',{'error':str(e)})
        await asyncio.sleep(2)
    raise AssertionError('Editor never ready after reload')
asyncio.run(main())
