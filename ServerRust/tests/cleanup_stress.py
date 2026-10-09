"""Bounded, loopback-only cleanup and adversarial-timeline regressions.

Run after cargo build, using the same environment as contract_http.py.
Ownership invariants: MCP sessions own in-flight request IDs and SSE lifetime;
HTTP request futures own cancellation; a hub connection owns only its commands;
shutdown owns closure of every socket, including unregistered connections.

Normal timeline: initialize -> call -> reply -> delete. Alternate timelines:
cancel -> late/duplicate callback -> ID reuse; disconnect -> ID reuse; editor
replacement -> retry old call -> new healthy call; delete/shutdown -> close all
retained streams and blocked calls; malformed initialize -> no allocated session.

Linux /proc RSS/FD samples are observations, never leak-freedom proof.
"""
import asyncio
import json
import signal
from pathlib import Path
import unittest

import httpx
import websockets
from contract_http import Server, Editor


class ControlledEditor(Editor):
    async def respond(self):
        async for data in self.ws:
            msg = json.loads(data)
            if msg["type"] == "ping":
                await self.ws.send(json.dumps({"type": "pong"}))
            elif msg["type"] == "execute":
                await self.calls.put(msg)
                if self.block and msg["name"] == "manage_scene":
                    continue
                result = {"success": True, "data": {"project": self.name, "wire_command": msg["name"], "wire_params": msg["params"]}}
                if msg["name"] == "ping":
                    result = {"success": True, "message": "pong"}
                await self.ws.send(json.dumps({"type": "command_result", "id": msg["id"], "result": result}))


class CleanupStress(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.server = Server()
        await self.server.ready()
        self.client = httpx.AsyncClient(base_url=self.server.url, timeout=8)
        self.editors = []
        self.tasks = []
        self.ident = 0

    async def asyncTearDown(self):
        for task in self.tasks:
            task.cancel()
        await asyncio.gather(*self.tasks, return_exceptions=True)
        for editor in self.editors:
            await editor.close()
        await self.client.aclose()
        self.server.close()

    async def rpc(self, method, params=None, sid=None, ident=None):
        self.ident += 1
        return await self.client.post('/mcp', headers={'Mcp-Session-Id': sid} if sid else {},
            json={'jsonrpc': '2.0', 'id': self.ident if ident is None else ident,
                  'method': method, 'params': params or {}})

    async def session(self):
        reply = await self.rpc('initialize', {'protocolVersion': '2025-03-26', 'capabilities': {}, 'clientInfo': {'name': 'cleanup-test', 'version': '1'}})
        self.assertEqual(reply.status_code, 200, reply.text)
        return reply.headers['mcp-session-id']

    async def editor(self, name='Stress', project='abc12345'):
        editor = await ControlledEditor(self.server, name, project).start()
        self.editors.append(editor)
        return editor

    def pending(self, sid, ident):
        task = asyncio.create_task(self.rpc('tools/call', {'name': 'manage_scene', 'arguments': {'action': 'get_hierarchy'}}, sid, ident))
        self.tasks.append(task)
        return task

    async def cancel(self, sid, ident):
        return await self.client.post('/mcp', headers={'Mcp-Session-Id': sid}, json={'jsonrpc': '2.0', 'method': 'notifications/cancelled', 'params': {'requestId': ident}})

    async def next_command(self, editor):
        async with asyncio.timeout(5):
            while True:
                command = await editor.calls.get()
                if command['name'] == 'manage_scene':
                    return command

    def resources(self):
        import sys
        if sys.platform == 'darwin':
            import psutil
            process = psutil.Process(self.server.process.pid)
            return {'rss_kib': process.memory_info().rss // 1024, 'fds': process.num_fds()}
        proc = Path('/proc') / str(self.server.process.pid)
        rss = next(int(line.split()[1]) for line in (proc / 'status').read_text().splitlines() if line.startswith('VmRSS:'))
        return {'rss_kib': rss, 'fds': len(list((proc / 'fd').iterdir()))}

    async def test_session_churn_releases_capacity(self):
        samples = []
        for wave in range(4):
            for _ in range(75):
                sid = await self.session()
                reply = await self.client.delete('/mcp', headers={'Mcp-Session-Id': sid})
                self.assertEqual(reply.status_code, 200)
                self.assertEqual((await self.rpc('ping', sid=sid)).status_code, 404)
            await asyncio.sleep(.05)
            samples.append(self.resources())
        print('300 create/delete session samples:', samples, flush=True)
        self.assertLessEqual(samples[-1]['fds'], samples[0]['fds'] + 3)

    async def test_cancel_late_duplicate_result_and_reuse_id(self):
        editor = await self.editor()
        editor.block = True
        sid = await self.session()
        for _ in range(25):
            task = self.pending(sid, 700)
            command = await self.next_command(editor)
            self.assertEqual((await self.cancel(sid, 700)).status_code, 202)
            result = (await asyncio.wait_for(task, 3)).json()
            self.assertEqual(result['error']['code'], -32800, result)
            stale = {'type': 'command_result', 'id': command['id'], 'result': {'success': True, 'data': {'stale': True}}}
            await editor.ws.send(json.dumps(stale))
            await editor.ws.send(json.dumps(stale))
            self.assertEqual((await self.rpc('ping', sid=sid, ident=700)).json()['result'], {})
        editor.block = False
        reply = (await self.pending(sid, 700)).json()
        self.assertNotIn('error', reply, reply)
        self.assertNotIn('stale', json.dumps(reply))

    async def test_delete_cancels_inflight_and_closes_sse(self):
        editor = await self.editor()
        editor.block = True
        sid = await self.session()
        async with self.client.stream('GET', '/mcp', headers={'Mcp-Session-Id': sid, 'Accept': 'text/event-stream'}) as stream:
            self.assertEqual(stream.status_code, 200)
            async def drain():
                async for _ in stream.aiter_bytes():
                    pass
            drained = asyncio.create_task(drain())
            self.tasks.append(drained)
            task = self.pending(sid, 701)
            await self.next_command(editor)
            self.assertEqual((await self.client.delete('/mcp', headers={'Mcp-Session-Id': sid})).status_code, 200)
            result = (await asyncio.wait_for(task, 3)).json()
            self.assertEqual(result['error']['code'], -32800, result)
            await asyncio.wait_for(drained, 3)
            self.assertEqual((await self.rpc('ping', sid=sid)).status_code, 404)

    async def test_replacement_fails_old_call_and_preserves_new_editor(self):
        sid = await self.session()
        old = await self.editor()
        for n in range(15):
            old.block = True
            while not old.calls.empty():
                old.calls.get_nowait()
            task = self.pending(sid, 800 + n)
            await self.next_command(old)
            new = await self.editor(name=f'Replacement{n}')
            reply = (await asyncio.wait_for(task, 3)).json()
            self.assertIn('retry', json.dumps(reply), reply)
            await asyncio.wait_for(old.ws.wait_closed(), 3)
            healthy = (await self.pending(sid, 900 + n)).json()
            self.assertIn(f'Replacement{n}', json.dumps(healthy), healthy)
            old = new
        print('After 15 replacements:', self.resources(), flush=True)

    async def test_dropped_http_request_frees_request_slot(self):
        editor = await self.editor()
        editor.block = True
        sid = await self.session()
        for n in range(12):
            reader, writer = await asyncio.open_connection('127.0.0.1', self.server.port)
            body = json.dumps({'jsonrpc': '2.0', 'id': 4242, 'method': 'tools/call', 'params': {'name': 'manage_scene', 'arguments': {'action': 'get_hierarchy'}}}).encode()
            writer.write(f'POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{self.server.port}\r\nMcp-Session-Id: {sid}\r\nContent-Type: application/json\r\nContent-Length: {len(body)}\r\n\r\n'.encode() + body)
            await writer.drain()
            await self.next_command(editor)
            writer.transport.abort()
            await writer.wait_closed()
            for _ in range(40):
                reply = (await self.rpc('ping', sid=sid, ident=4242)).json()
                if 'result' in reply:
                    break
                await asyncio.sleep(.025)
            self.assertEqual(reply.get('result'), {}, reply)
        print('After 12 aborted HTTP calls:', self.resources(), flush=True)

    async def test_sigint_shutdown_closes_active_and_unregistered_sockets(self):
        editor = await self.editor()
        editor.block = True
        sid = await self.session()
        unregistered = await websockets.connect(f'ws://127.0.0.1:{self.server.port}/hub/plugin')
        try:
            self.assertEqual(json.loads(await unregistered.recv())['type'], 'welcome')
            async with self.client.stream('GET', '/mcp', headers={'Mcp-Session-Id': sid, 'Accept': 'text/event-stream'}) as stream:
                async def drain():
                    async for _ in stream.aiter_bytes():
                        pass
                drained = asyncio.create_task(drain())
                self.tasks.append(drained)
                task = self.pending(sid, 702)
                await self.next_command(editor)
                self.server.process.send_signal(signal.SIGINT)
                reply = (await asyncio.wait_for(task, 5)).json()
                self.assertTrue('error' in reply or 'retry' in json.dumps(reply), reply)
                await asyncio.wait_for(drained, 5)
                await asyncio.wait_for(editor.ws.wait_closed(), 5)
                await asyncio.wait_for(unregistered.wait_closed(), 5)
                code = await asyncio.to_thread(self.server.process.wait, 5)
                self.assertEqual(code, 0, ''.join(self.server.stderr))
        finally:
            await unregistered.close()

    async def test_invalid_initialize_does_not_allocate_session(self):
        for invalid in ({'jsonrpc': '1.0', 'id': 1, 'method': 'initialize'},
                        {'jsonrpc': '2.0', 'id': {}, 'method': 'initialize'},
                        {'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': 'bad'}):
            reply = await self.client.post('/mcp', json=invalid)
            self.assertNotIn('mcp-session-id', reply.headers, (invalid, reply.text))
            self.assertIn('error', reply.json(), (invalid, reply.text))
        sid = await self.session()
        for invalid in (None, [], 12, 'bad', {'id': 4}, {'jsonrpc': '2.0', 'id': [], 'method': 'ping'}):
            reply = await self.client.post('/mcp', headers={'Mcp-Session-Id': sid, 'Content-Type': 'application/json'}, content=json.dumps(invalid))
            self.assertEqual(reply.json()['error']['code'], -32600, (invalid, reply.text))
        self.assertEqual((await self.rpc('ping', sid=sid)).json()['result'], {})


if __name__ == '__main__':
    unittest.main(verbosity=2)
