import asyncio,json,os,pathlib
from mcp import ClientSession,StdioServerParameters
from mcp.client.stdio import stdio_client
ROOT=pathlib.Path(__file__).resolve().parents[1];OUT=pathlib.Path(os.environ['REVIEW_EVIDENCE']);os.environ.update(json.loads((OUT/'live-environment.json').read_text()))
records=[]
async def main():
 p=StdioServerParameters(command=str(ROOT/'ServerRust/target/release/unity-mcp-light'),args=['--transport','stdio','--project-scoped-tools'],env={**os.environ,'DISABLE_TELEMETRY':'true'})
 async with stdio_client(p) as (r,w):
  async with ClientSession(r,w) as s:
   await s.initialize()
   async def call(name,args):
    v=await s.call_tool(name,args);data=v.structured_content
    if data is None:data=json.loads(next(c.text for c in v.content if c.type=='text'))
    if set(data)=={'result'}:data=data['result']
    records.append({'tool':name,'args':args,'result':data});(OUT/'live-optional.json').write_text(json.dumps(records,indent=2));print(name,json.dumps(data)[:2000],flush=True)
    assert not v.is_error and data.get('success',True),data
    return data
   await call('set_active_instance',{'instance':'unity-mcp-independent-review@987fb840'})
   info=(await s.read_resource('mcpforunity://project/info')).model_dump(mode='json',by_alias=True);assert 'unity-mcp-independent-review' in json.dumps(info)
   if not os.environ.get('REVIEW_RESUME_OPTIONAL'):
    await call('manage_graphics',{'action':'volume_create_profile','path':'Assets/IndependentSmoke/RealVolume.asset'})
    await call('manage_graphics',{'action':'volume_create','name':'IndependentVolume','profile_path':'Assets/IndependentSmoke/RealVolume.asset'})
    await call('manage_graphics',{'action':'volume_add_effect','target':'IndependentVolume','effect':'Bloom'})
    await call('manage_graphics',{'action':'volume_get_info','target':'IndependentVolume'})
   if os.environ.get('REVIEW_RESUME_OPTIONAL'):
    await call('refresh_unity',{'mode':'force','scope':'all','compile':'request','wait_for_ready':False})
    await asyncio.sleep(5)
   await call('execute_custom_tool',{'tool_name':'runtime_compilation','parameters':{'action':'execute_with_roslyn','class_name':'IndependentGenerated','code':'using UnityEngine; public class IndependentGenerated { public static void Run(GameObject host) { Debug.Log("Independent synthetic Roslyn execution"); } }'}})
asyncio.run(main())
