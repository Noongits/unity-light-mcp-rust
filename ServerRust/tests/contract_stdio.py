"""Black-box contract regression tests. Requires only Python's standard library.

UNITY_MCP_RUST_BIN=ServerRust/target/debug/unity-mcp-light python ServerRust/tests/contract_stdio.py
Tests use a fake Unity TCP editor; no real project or Unity installation is touched.
"""
import json
import os
import queue
import socket
import struct
import subprocess
import tempfile
import threading
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BINARY = Path(os.environ.get('UNITY_MCP_RUST_BIN', ROOT/'ServerRust/target/debug/unity-mcp-light')).resolve()

class UnityStub:
    def __init__(self):
        self.socket = socket.socket()
        self.socket.bind(('127.0.0.1', 0))
        self.socket.listen()
        self.port = self.socket.getsockname()[1]
        self.calls = queue.Queue()
        self.closed = False
        self.clients = []
        threading.Thread(target=self.accept, daemon=True).start()
    def accept(self):
        while not self.closed:
            try:
                client, _ = self.socket.accept()
            except OSError:
                return
            self.clients.append(client)
            threading.Thread(target=self.serve, args=(client,), daemon=True).start()
    def serve(self, client):
        def read(n):
            data=b''
            while len(data)<n:
                more=client.recv(n-len(data))
                if not more: raise EOFError
                data+=more
            return data
        try:
            client.sendall(b'MCP/0.1 FRAMING=1\n')
            while True:
                size=struct.unpack('>Q',read(8))[0]
                if size == 0: continue
                req=json.loads(read(size))
                self.calls.put(req)
                if req['type']=='ping':
                    result={'type':'pong'}
                else:
                    result={'success':True,'data':{'wire_command':req['type'],'wire_params':req.get('params',{})}}
                payload=json.dumps(result).encode()
                client.sendall(struct.pack('>Q',0)+struct.pack('>Q',len(payload))+payload)
        except (OSError, EOFError):
            pass
    def close(self):
        self.closed=True
        self.socket.close()
        for client in self.clients:
            client.close()

class Contract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not BINARY.exists(): raise unittest.SkipTest(f'Build Rust binary first: {BINARY}')
        cls.tmp=tempfile.TemporaryDirectory()
        cls.unity=UnityStub()
        (Path(cls.tmp.name)/'unity-mcp-status-deadbeef.json').write_text(json.dumps({
            'unity_port':cls.unity.port,'project_hash':'deadbeef','project_name':'ContractProject',
            'project_path':str(Path(cls.tmp.name)/'ContractProject'/'Assets'),'unity_version':'6000.0.1f1'}))
        env={**os.environ,'UNITY_MCP_STATUS_DIR':cls.tmp.name,'UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S':'0','DISABLE_TELEMETRY':'true'}
        cls.process=subprocess.Popen([str(BINARY),'--transport','stdio','--project-scoped-tools'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,env=env)
        cls.output=queue.Queue()
        cls.stderr=[]
        threading.Thread(target=lambda:[cls.output.put(line) for line in cls.process.stdout],daemon=True).start()
        threading.Thread(target=lambda:[cls.stderr.append(line) for line in cls.process.stderr],daemon=True).start()
        cls.counter=0
        init=cls.rpc('initialize',{'protocolVersion':'2025-03-26','capabilities':{},'clientInfo':{'name':'contract-test','version':'1'}})
        assert init['result']['serverInfo']['name']=='unity-mcp-light',init
        cls.notify('notifications/initialized',{})
    @classmethod
    def tearDownClass(cls):
        cls.process.terminate()
        cls.process.wait(timeout=5)
        cls.process.stdin.close();cls.process.stdout.close();cls.process.stderr.close()
        cls.unity.close()
        cls.tmp.cleanup()
    @classmethod
    def notify(cls,method,params):
        cls.process.stdin.write(json.dumps({'jsonrpc':'2.0','method':method,'params':params})+'\n');cls.process.stdin.flush()
    @classmethod
    def rpc(cls,method,params=None):
        cls.counter+=1
        ident=cls.counter
        cls.process.stdin.write(json.dumps({'jsonrpc':'2.0','id':ident,'method':method,'params':params or {}})+'\n');cls.process.stdin.flush()
        while True:
            try: line=cls.output.get(timeout=15)
            except queue.Empty: raise AssertionError(f'No response to {method}: {"".join(cls.stderr)[-4000:]}')
            try: result=json.loads(line)
            except ValueError: raise AssertionError(f'Non-JSON output on MCP stdout: {line!r}')
            if result.get('id')==ident:return result
    def call(self,name,arguments):
        reply=self.rpc('tools/call',{'name':name,'arguments':arguments})
        self.assertNotIn('error',reply,reply)
        result=reply['result']
        if 'structuredContent' in result:
            value=result['structuredContent']
            return value['result'] if set(value)=={'result'} else value
        self.assertFalse(result.get('isError'),result)
        return json.loads(result['content'][0]['text'])
    def test_01_exact_tool_contract(self):
        expected=json.loads((ROOT/'ServerRust/contracts/tools.json').read_text())
        for item in expected:
            item.pop('group',None);item.pop('unity_target',None)
        actual=self.rpc('tools/list')['result']['tools']
        self.assertEqual({t['name']:t for t in expected},{t['name']:t for t in actual})
    def test_02_exact_resource_contract(self):
        expected=json.loads((ROOT/'ServerRust/contracts/resources.json').read_text())
        actual=self.rpc('resources/list')['result']['resources']
        templates=self.rpc('resources/templates/list')['result']['resourceTemplates']
        self.assertEqual({x['name']:x for x in expected},{x['name']:x for x in actual+templates})
    def test_03_static_docs(self):
        expected=json.loads((ROOT/'ServerRust/contracts/static_resources.json').read_text())
        for uri,want in expected.items():
            result=self.rpc('resources/read',{'uri':uri})
            self.assertEqual(want,json.loads(result['result']['contents'][0]['text']))
    def test_04_resource_bridge_framing(self):
        result=self.rpc('resources/read',{'uri':'mcpforunity://scene/gameobject/-321/component/Camera'})
        payload=json.loads(result['result']['contents'][0]['text'])
        self.assertEqual(payload['data']['wire_command'],'get_gameobject_component')
        self.assertEqual(payload['data']['wire_params'],{'instanceID':-321,'componentName':'Camera'})
    def test_05_no_prompts(self):
        self.assertEqual(self.rpc('prompts/list')['result']['prompts'],[])
    def test_06_unknown_method(self):
        self.assertEqual(self.rpc('does/not/exist')['error']['code'],-32601)
    def test_07_group_visibility_and_reset(self):
        self.call('manage_tools',{'action':'deactivate','group':'testing'})
        names={t['name'] for t in self.rpc('tools/list')['result']['tools']}
        self.assertNotIn('run_tests',names)
        self.assertIn('manage_tools',names)
        self.call('manage_tools',{'action':'activate','group':'testing'})
        self.assertIn('run_tests',{t['name'] for t in self.rpc('tools/list')['result']['tools']})
        self.call('manage_tools',{'action':'reset'})
    def test_09_python_forwarding_parity(self):
        cases=json.loads((ROOT/'ServerRust/contracts/forwarding_wire.json').read_text())
        for case in cases:
            with self.subTest(tool=case['tool'],arguments=case['arguments']):
                while not self.unity.calls.empty():self.unity.calls.get_nowait()
                if not case.get('valid',True):
                    result=self.rpc('tools/call',{'name':case['tool'],'arguments':case['arguments']})
                    self.assertTrue(result['result'].get('isError'),result)
                    continue
                self.call(case['tool'],case['arguments'])
                observed=[]
                while not self.unity.calls.empty():
                    call=self.unity.calls.get_nowait()
                    if call['type'] not in ('ping','get_tool_states','get_editor_state','get_project_info'):
                        observed.append({'command':call['type'],'params':call.get('params',{})})
                self.assertEqual(case['calls'],observed)
    def test_08_invalid_resource_id(self):
        result=self.rpc('resources/read',{'uri':'mcpforunity://scene/gameobject/not-an-integer'})
        self.assertFalse(json.loads(result['result']['contents'][0]['text'])['success'])

if __name__=='__main__':unittest.main(verbosity=2)
