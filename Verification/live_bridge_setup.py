import pathlib,socket,json,os
out=pathlib.Path(os.environ['REVIEW_EVIDENCE'])
status=pathlib.Path('/tmp/unity-mcp-review-live-status');status.mkdir(exist_ok=True)
with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
x={'UNITY_MCP_STATUS_DIR':str(status),'REVIEW_BRIDGE_PORT':str(port),'REVIEW_BRIDGE_READY':str(out/'live-ready.txt'),'REVIEW_BRIDGE_STOP':str(out/'live-stop.txt'),'REVIEW_EVIDENCE':str(out)}
for k in ['REVIEW_BRIDGE_READY','REVIEW_BRIDGE_STOP']:pathlib.Path(x[k]).unlink(missing_ok=True)
(out/'live-environment.json').write_text(json.dumps(x,indent=2))
