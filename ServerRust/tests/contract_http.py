"""HTTP MCP and real WebSocket regression tests against the built Rust binary.

Run with Server dependencies: python ServerRust/tests/contract_http.py
Uses loopback-only fake editors and a fake API-key validation service.
"""
import asyncio
import contextlib
import http.server
import json
import os
import socket
import subprocess
import threading
import unittest
from pathlib import Path
import httpx
import websockets

# Tests only access loopback; ignore ambient proxy settings.
for _key in list(os.environ):
    if _key.lower().endswith('_proxy'):os.environ.pop(_key)
ROOT=Path(__file__).resolve().parents[2]
BINARY=Path(os.environ.get('UNITY_MCP_RUST_BIN',ROOT/'ServerRust/target/debug/unity-mcp-light')).resolve()
def port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
class Server:
    def __init__(self,*extra):
        self.port=port();self.url=f'http://127.0.0.1:{self.port}'
        self.stderr=[]
        self.process=subprocess.Popen([str(BINARY),'--transport','http','--http-port',str(self.port),'--project-scoped-tools',*extra],stdout=subprocess.DEVNULL,stderr=subprocess.PIPE,text=True,env={**os.environ,'UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S':'0'})
        threading.Thread(target=lambda:[self.stderr.append(s) for s in self.process.stderr],daemon=True).start()
    async def ready(self):
        async with httpx.AsyncClient() as client:
            for _ in range(100):
                try:
                    if (await client.get(self.url+'/health')).status_code==200:return
                except httpx.HTTPError:pass
                if self.process.poll() is not None:raise AssertionError(''.join(self.stderr))
                await asyncio.sleep(.05)
        raise AssertionError('HTTP server startup timed out: '+''.join(self.stderr))
    def close(self):
        self.process.terminate();self.process.wait(timeout=5)
        self.process.stderr.close()
class Editor:
    def __init__(self,server,name,hash,key=None):self.server=server;self.name=name;self.hash=hash;self.key=key;self.calls=asyncio.Queue();self.block=False
    async def start(self):
        self.ws=await websockets.connect(f'ws://127.0.0.1:{self.server.port}/hub/plugin',additional_headers={'X-API-Key':self.key} if self.key else None)
        self.welcome=json.loads(await self.ws.recv())
        await self.ws.send(json.dumps({'type':'register','project_name':self.name,'project_hash':self.hash,'unity_version':'6000.0.1f1'}))
        self.registration=json.loads(await self.ws.recv())
        self.task=asyncio.create_task(self.respond());return self
    async def respond(self):
        async for data in self.ws:
            msg=json.loads(data)
            if msg['type']=='ping':await self.ws.send(json.dumps({'type':'pong'}))
            elif msg['type']=='execute':
                await self.calls.put(msg)
                if self.block and msg['name']!='ping':continue
                result={'success':True,'message':'pong'} if msg['name']=='ping' else {'success':True,'data':{'project':self.name,'wire_command':msg['name'],'wire_params':msg['params']}}
                await self.ws.send(json.dumps({'type':'command_result','id':msg['id'],'result':result}))
    async def close(self):
        await self.ws.close();self.task.cancel()
        with contextlib.suppress(asyncio.CancelledError,websockets.exceptions.ConnectionClosed):await self.task

class HTTPContract(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        if not BINARY.exists():self.skipTest('Build Rust binary first')
        self.server=Server();await self.server.ready();self.client=httpx.AsyncClient(base_url=self.server.url,timeout=15)
        self.editors=[];self.id=0
    async def asyncTearDown(self):
        for editor in self.editors:await editor.close()
        await self.client.aclose();self.server.close()
    async def rpc(self,method,params=None,sid=None,key=None,ident=None):
        self.id+=1;headers={}
        if sid:headers['Mcp-Session-Id']=sid
        if key:headers['X-API-Key']=key
        return await self.client.post('/mcp',headers=headers,json={'jsonrpc':'2.0','id':ident or self.id,'method':method,'params':params or {}})
    async def session(self,key=None):
        reply=await self.rpc('initialize',{'protocolVersion':'2025-03-26','capabilities':{},'clientInfo':{'name':'same-client','version':'1'}},key=key)
        self.assertEqual(reply.status_code,200,reply.text)
        return reply.headers['mcp-session-id']
    async def editor(self,name,hash,key=None):
        editor=await Editor(self.server,name,hash,key).start();self.editors.append(editor);return editor
    async def call(self,sid,name,args,key=None):
        result=(await self.rpc('tools/call',{'name':name,'arguments':args},sid,key)).json()
        self.assertNotIn('error',result,result)
        payload=result['result']
        return payload.get('structuredContent') or json.loads(payload['content'][0]['text'])
    async def test_sdk_initialization(self):
        from mcp import ClientSession
        from mcp.client.streamable_http import streamable_http_client
        async with streamable_http_client(self.server.url+'/mcp') as (read,write,_):
            async with ClientSession(read,write) as session:
                result=await session.initialize();self.assertEqual(result.serverInfo.name,'unity-mcp-light')
                tools=await session.list_tools();self.assertIn('manage_scene',{t.name for t in tools.tools})
                resources=await session.list_resources();self.assertIn('unity_instances',{r.name for r in resources.resources})
    async def test_session_instance_and_group_isolation(self):
        a=await self.editor('First','11111111');b=await self.editor('Second','22222222')
        self.assertEqual(a.welcome['type'],'welcome');self.assertEqual(a.registration['type'],'registered')
        one,two=await self.session(),await self.session();self.assertNotEqual(one,two)
        await self.call(one,'set_active_instance',{'instance':'First@11111111'})
        await self.call(two,'set_active_instance',{'instance':'Second@22222222'})
        r1=await self.call(one,'manage_scene',{'action':'get_hierarchy'})
        r2=await self.call(two,'manage_scene',{'action':'get_hierarchy'})
        self.assertEqual(r1['data']['project'],'First');self.assertEqual(r2['data']['project'],'Second')
        inline=await self.call(one,'manage_scene',{'action':'get_hierarchy','unity_instance':'Second@22222222'})
        self.assertEqual(inline['data']['project'],'Second')
        again=await self.call(one,'manage_scene',{'action':'get_hierarchy'})
        self.assertEqual(again['data']['project'],'First')
        await self.call(one,'manage_tools',{'action':'activate','group':'testing'})
        names1={t['name'] for t in (await self.rpc('tools/list',sid=one)).json()['result']['tools']}
        names2={t['name'] for t in (await self.rpc('tools/list',sid=two)).json()['result']['tools']}
        self.assertIn('run_tests',names1);self.assertNotIn('run_tests',names2)
    async def test_cancellation_and_session_delete(self):
        editor=await self.editor('Blocked','deadbeef');sid=await self.session();editor.block=True
        pending=asyncio.create_task(self.rpc('tools/call',{'name':'manage_scene','arguments':{'action':'get_hierarchy'}},sid,ident=900))
        await asyncio.wait_for(editor.calls.get(),5)
        reply=await self.client.post('/mcp',headers={'Mcp-Session-Id':sid},json={'jsonrpc':'2.0','method':'notifications/cancelled','params':{'requestId':900,'reason':'test'}})
        self.assertEqual(reply.status_code,202)
        result=(await asyncio.wait_for(pending,5)).json();self.assertEqual(result['error']['code'],-32800)
        self.assertEqual((await self.client.delete('/mcp',headers={'Mcp-Session-Id':sid})).status_code,200)
        self.assertEqual((await self.rpc('tools/list',sid=sid)).status_code,404)
    async def test_clean_websocket_close(self):
        editor=await self.editor('Closing','abcdef12')
        await editor.ws.close(code=1000)
        self.assertEqual(editor.ws.close_code,1000)

    async def test_custom_tool_registration(self):
        editor=await self.editor('Custom','abcdef01');sid=await self.session()
        await self.call(sid,'set_active_instance',{'instance':'Custom@abcdef01'})
        await editor.ws.send(json.dumps({'type':'register_tools','tools':[{'name':'test_custom','description':'Test custom tool','parameters':[{'name':'count','type':'int','required':True}]}]}))
        for _ in range(30):
            tools=(await self.rpc('tools/list',sid=sid)).json()['result']['tools']
            tool=next((t for t in tools if t['name']=='test_custom'),None)
            if tool:break
            await asyncio.sleep(.05)
        self.assertIsNotNone(tool)
        self.assertEqual(tool['inputSchema']['properties']['count']['type'],'integer')
        result=await self.call(sid,'test_custom',{'count':2})
        self.assertEqual(result['data']['wire_command'],'test_custom')
        self.assertEqual(result['data']['wire_params'],{'count':2})
        resource=(await self.rpc('resources/read',{'uri':'mcpforunity://custom-tools'},sid)).json()
        data=json.loads(resource['result']['contents'][0]['text'])['data']
        self.assertEqual(data['project_id'],'abcdef01');self.assertEqual(data['tool_count'],1)
    async def test_rest_and_origin(self):
        await self.editor('Rest','aabbccdd')
        reply=await self.client.post('/api/command',json={'type':'get_tags','params':{},'unity_instance':'Rest@aabbccdd'})
        self.assertEqual(reply.status_code,200,reply.text);self.assertEqual(reply.json()['data']['project'],'Rest')
        self.assertEqual((await self.client.post('/mcp',headers={'Origin':'https://evil.invalid'},json={'jsonrpc':'2.0','id':1,'method':'initialize'})).status_code,403)
    async def test_remote_auth_and_cross_user_isolation(self):
        class Auth(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                key=json.loads(self.rfile.read(int(self.headers['Content-Length'])))['api_key']
                data=json.dumps({'valid':key in ('alice','bob'),'user_id':key}).encode()
                self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
            def log_message(self,*args):pass
        auth=http.server.ThreadingHTTPServer(('127.0.0.1',0),Auth)
        threading.Thread(target=auth.serve_forever,daemon=True).start()
        await self.client.aclose();self.server.close()
        self.server=Server('--http-remote-hosted','--api-key-validation-url',f'http://127.0.0.1:{auth.server_port}/validate');await self.server.ready();self.client=httpx.AsyncClient(base_url=self.server.url,timeout=15)
        try:
            self.assertEqual((await self.rpc('initialize')).status_code,401)
            self.assertEqual((await self.rpc('initialize',key='invalid')).status_code,401)
            private_a=await self.editor('PrivateA','11111111','alice');await self.editor('PrivateB','11111111','bob')
            sid=await self.session('alice');other=await self.session('bob')
            self.assertEqual((await self.rpc('tools/list',sid=sid,key='bob')).status_code,404)
            result=(await self.rpc('resources/read',{'uri':'mcpforunity://instances'},sid,'alice')).json()
            content=json.loads(result['result']['contents'][0]['text'])
            self.assertEqual([i['name'] for i in content['instances']],['PrivateA'])
            await self.call(sid,'set_active_instance',{'instance':'PrivateA@11111111'},'alice')
            state=await self.rpc('resources/read',{'uri':'mcpforunity://editor/state'},sid,'alice')
            self.assertEqual(state.status_code,200)
            calls=[]
            while not private_a.calls.empty():calls.append(private_a.calls.get_nowait()['name'])
            self.assertIn('get_editor_state',calls)
            self.assertNotIn('get_project_info',calls,'Remote project paths must not trigger host filesystem scanning')
            self.assertEqual((await self.client.get('/api/instances')).status_code,404)
        finally:await asyncio.to_thread(auth.shutdown);auth.server_close()
if __name__=='__main__':unittest.main(verbosity=2)
