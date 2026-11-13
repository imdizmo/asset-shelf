"""Opens an Asset Shelf asset in a fresh Blender window.

    blender --python open_in_blender.py -- job.json

The job is the same as the bridge's (name, files, textures). The scene is emptied, the files imported with the
bridge's code (textures linked), the view framed on them in Material Preview, and the job file deleted.
"""
import json
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import asset_shelf_bridge as bridge  # noqa: E402

job_path = sys.argv[sys.argv.index('--') + 1]


def frame_view():
    win = bpy.context.window_manager.windows[0] if bpy.context.window_manager.windows else None
    if win is None:
        return 0.2  # the window is not up yet: try again shortly
    quiet_prefs(lambda p: setattr(p.view, 'show_splash', True))
    for area in win.screen.areas:
        if area.type != 'VIEW_3D':
            continue
        area.spaces.active.shading.type = 'MATERIAL'
        region = next((r for r in area.regions if r.type == 'WINDOW'), None)
        if region:
            with bpy.context.temp_override(window=win, area=area, region=region):
                bpy.ops.view3d.view_selected()
    return None


def quiet_prefs(fn):
    # the change must not reach the saved preferences (Blender saves dirty preferences on quit)
    prefs = bpy.context.preferences
    fn(prefs)
    prefs.is_dirty = False


def main():
    quiet_prefs(lambda p: setattr(p.view, 'show_splash', False))  # this window only; put back once it is up
    bpy.ops.wm.read_homefile(use_empty=True)
    try:
        name, n = bridge.run_job(job_path)
        print(f'Asset Shelf: opened {name} ({n} objects)')
    finally:
        try:
            os.remove(job_path)
        except OSError:
            pass
    bpy.app.timers.register(frame_view, first_interval=0.5)


main()
