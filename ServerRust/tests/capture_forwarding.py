"""Capture command-name/argument transformations from the Python reference."""
import asyncio
import importlib
import inspect
import json
import os
import sys
from pathlib import Path
from unittest.mock import AsyncMock
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'Server/src'))
os.environ['DISABLE_TELEMETRY']='true'
CASES=[
 ('find_gameobjects',{'search_term':'Cube','page_size':'10','include_inactive':'false'}),
 ('manage_gameobject',{'action':'create','name':'Cube','position':'[1, 2, 3]','set_active':'false'}),
 ('manage_gameobject',{'action':'modify','target':'Cube','is_static':'true','component_properties':'{"Transform":{"localScale":[2,2,2]}}'}),
 ('manage_scene',{'action':'get_hierarchy','page_size':'20','cursor':'2','include_transform':'true'}),
 ('manage_scene',{'action':'load','name':'Main','additive':'true'}),
 ('manage_asset',{'action':'search','path':'Assets','page_size':'10','page_number':'2'}),
 ('manage_asset',{'action':'modify','path':'Assets/Test.mat','properties':'{"foo":true}'}),
 ('manage_components',{'action':'add','target':'Cube','component_type':'Rigidbody'}),
 ('manage_components',{'action':'set_property','target':'Cube','component_type':'Rigidbody','property':'mass','value':'2'}),
 ('manage_editor',{'action':'add_tag','tag_name':'Enemy'}),
 ('manage_camera',{'action':'list_cameras'}),
 ('manage_graphics',{'action':'skybox_set_fog','fog_enabled':True,'fog_density':0.01}),
 ('manage_physics',{'action':'get_settings','dimension':'3d'}),
 ('manage_material',{'action':'set_material_color','material_path':'Assets/Test.mat','color':[1,0,0,1]}),
 ('manage_shader',{'action':'read','name':'Test','path':'Assets'}),
 ('manage_texture',{'action':'create','path':'Assets/Test.png','width':'16','height':'16','fill_color':'[255,0,0,255]'}),
 ('manage_prefabs',{'action':'get_info','prefab_path':'Assets/Test.prefab'}),
 ('manage_ui',{'action':'get_visual_tree','target':'UIDocument','max_depth':'3'}),
 ('read_console',{'count':'5','include_stacktrace':'false'}),
 ('execute_menu_item',{'menu_path':'Window/General/Console'}),
 ('unity_reflect',{'action':'get_type','class_name':'UnityEngine.GameObject'}),
]
CASES.extend([
 ('manage_scene',dict(action='get_hierarchy',name='Main',path='Scenes/Main',build_index='1',scene_view_target=-12,parent='Root',page_size='25',cursor='5',max_nodes='50',max_depth='3',max_children_per_node='10',include_transform='false',scene_name='Other',scene_path='Assets/Other.unity',target=-42,remove_scene='false',additive='true',template='3d_basic',auto_repair='true')),
 ('manage_camera',dict(action='screenshot',target='Main Camera',search_method='by_name',properties='{"fov":45}',screenshot_file_name='capture.png',screenshot_super_size='2',camera='Main Camera',include_image='false',max_resolution='512',capture_source='game_view',batch='orbit',view_target=[1,2,3],view_position='[3,2,1]',view_rotation='[0,90,0]',orbit_angles='8',orbit_elevations='[0,30,-15]',orbit_distance='5.5',orbit_fov='60',output_folder='Captures')),
 ('manage_prefabs',dict(action='modify_contents',prefab_path='Assets/Test.prefab',target='Root',allow_overwrite=True,search_inactive=False,unlink_if_instance=True,position={'x':1,'y':2,'z':3},rotation='[0,90,0]',scale=[2,2,2],name='Renamed',tag='Enemy',layer='Default',set_active=False,parent='Parent',components_to_add=['Rigidbody'],components_to_remove=['BoxCollider'],create_child={'name':'Child','primitive_type':'Cube'},delete_child=['Old','Root/Other'],component_properties={'Rigidbody':{'mass':5}})),
 ('manage_texture',dict(action='create',path='Assets/Test.png',width=16,height=32,fill_color='#FF0080',pattern='checkerboard',palette='[[255,0,0,255],[0,255,0,255]]',pattern_size=4,gradient_type='linear',gradient_angle=90,noise_scale=0.1,octaves=3,set_pixels={'x':0,'y':0,'width':1,'height':1,'color':[1,0,0,1]},as_sprite={'pivot':[0.5,0.5],'pixels_per_unit':100},import_settings={'texture_type':'sprite','filter_mode':'point'})),
 ('manage_texture',dict(action='create_sprite',path='Assets/Sprite.png',as_sprite=True,fill_color={'r':1.0,'g':0.5,'b':0.0})),
 ('manage_ui',dict(action='get_visual_tree',path='Assets/UI.uxml',contents='<ui:UXML/>',target='UI',source_asset='Assets/UI.uxml',panel_settings='Assets/Panel.asset',sort_order='2',scale_mode='scale_with_screen_size',reference_resolution=[1920,1080],settings={'scale':1},max_depth='4',width='800',height='600',include_image='false',max_resolution='512',screenshot_file_name='ui.png',output_folder='Captures',stylesheet='Assets/UI.uss',filter_type='Button',page_size='20',page_number='2',element_name='Button',text='Hello',add_classes=['primary'],remove_classes=['old'],toggle_classes=['active'],style={'color':'red'},enabled='false',visible='true',tooltip='Help')),
 ('manage_material',dict(action='set_material_color',material_path='Assets/Test.mat',property='_BaseColor',color='[1, 0.5, 0, 1]')),
])
async def main():
    results=[]
    wire_results=[]
    from fastmcp.tools import FunctionTool
    for name,args in CASES:
        module=importlib.import_module('services.tools.'+name)
        calls=[]
        async def fake(send,instance,command,params,**kwargs):
            calls.append({'command':command,'params':params})
            return {'success':True,'data':{}}
        module.send_with_unity_instance=fake
        if hasattr(module,'unity_transport'): module.unity_transport.send_with_unity_instance=fake
        module.get_unity_instance_from_context=AsyncMock(return_value=None)
        ctx=AsyncMock()
        try:
            response=await inspect.unwrap(getattr(module,name))(ctx,**args)
            if not calls:
                print(f'SKIP {name} {args}: {response}',file=sys.stderr)
                continue
            results.append({'tool':name,'arguments':args,'calls':list(calls)})
            calls.clear()
            original=inspect.unwrap(getattr(module,name))
            async def wrapped(**kwargs):
                return await original(ctx,**kwargs)
            wrapped.__name__=name
            wrapped.__signature__=inspect.signature(original).replace(parameters=[p for p in inspect.signature(original).parameters.values() if p.name!='ctx'])
            wrapped.__annotations__={k:v for k,v in original.__annotations__.items() if k!='ctx'}
            tool=FunctionTool.from_function(wrapped)
            try:
                await tool.run(args)
                wire_results.append({'tool':name,'arguments':args,'calls':list(calls),'valid':True})
            except Exception as error:
                wire_results.append({'tool':name,'arguments':args,'calls':[],'valid':False,'validation_error':str(error)})
        except Exception as e:
            print(f'SKIP {name}: {e}',file=sys.stderr)
    out=ROOT/'ServerRust/contracts/forwarding.json'
    out.write_text(json.dumps(results,indent=2)+'\n')
    (out.parent/'forwarding_wire.json').write_text(json.dumps(wire_results,indent=2)+'\n')
    print(f'Captured {len(results)} forwarding cases')
if __name__=='__main__':asyncio.run(main())
