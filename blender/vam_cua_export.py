# Export to VAM CUA — minimal Blender bridge for Asset CUA Studio (by Shadow Venom)
#
# What it does: exports your selection as .glb and opens it straight in Asset CUA Studio,
# where you preview + tune (orientation, materials, collider, vertex colors, animation…) and
# bake the VAM CUA. It just removes the "export glb → open file browser → drag in" busywork —
# the actual conversion + tuning still happens in the app.
#
# Install:  Edit > Preferences > Add-ons > Install… > pick this .py > enable it.
#           Then expand it and set "Asset CUA Studio.exe" to the exe from the zip (Windows only).
# Use:      File > Export > VAM CUA (.glb → Asset CUA Studio)

bl_info = {
    "name": "Export to VAM CUA",
    "author": "Shadow Venom",
    "version": (0, 1, 0),
    "blender": (3, 6, 0),          # baseline target is 4.x; uses only cross-version-stable glTF params
    "location": "File > Export > VAM CUA (.glb → Asset CUA Studio)",
    "description": "Export selection as .glb and open it in Asset CUA Studio for VAM CUA conversion.",
    "category": "Import-Export",
}

import bpy
import os
import tempfile
import subprocess
from bpy.props import StringProperty, BoolProperty
from bpy.types import Operator, AddonPreferences


class ACUA_Prefs(AddonPreferences):
    bl_idname = __name__

    exe_path: StringProperty(
        name="Asset CUA Studio.exe",
        subtype="FILE_PATH",
        description="Path to AssetCuaStudio.exe (from the Asset CUA Studio zip)",
    )

    def draw(self, context):
        col = self.layout.column()
        col.prop(self, "exe_path")
        col.label(text="Windows only. Point this at AssetCuaStudio.exe from the zip.", icon="INFO")


def _exe(context):
    prefs = context.preferences.addons.get(__name__)
    return prefs.preferences.exe_path if prefs else ""


class ACUA_OT_export(Operator):
    bl_idname = "export_scene.vam_cua"
    bl_label = "VAM CUA (.glb → Asset CUA Studio)"
    bl_description = "Export selection as .glb and open it in Asset CUA Studio"
    bl_options = {"REGISTER"}

    selection_only: BoolProperty(
        name="Selection only",
        description="Export only the selected objects (off = whole scene)",
        default=True,
    )

    def execute(self, context):
        exe = _exe(context)
        if not exe or not os.path.isfile(exe):
            self.report({"ERROR"}, "Set the 'Asset CUA Studio.exe' path in Add-on Preferences first.")
            return {"CANCELLED"}

        if self.selection_only and not context.selected_objects:
            self.report({"ERROR"}, "Nothing selected. Select your model (or turn off 'Selection only').")
            return {"CANCELLED"}

        # name the .glb after the active object, else the .blend file, else 'model'
        base = (context.active_object.name if context.active_object else "") \
            or os.path.splitext(os.path.basename(bpy.data.filepath))[0] \
            or "model"
        out = os.path.join(tempfile.gettempdir(), bpy.path.clean_name(base) + ".glb")

        try:
            bpy.ops.export_scene.gltf(
                filepath=out,
                export_format="GLB",
                use_selection=self.selection_only,
                export_apply=True,          # apply modifiers for a clean bake
                export_animations=True,
                export_yup=True,
            )
        except Exception as e:
            self.report({"ERROR"}, "glb export failed: %s" % e)
            return {"CANCELLED"}

        try:
            subprocess.Popen([exe, out])
        except Exception as e:
            self.report({"ERROR"}, "Could not launch Asset CUA Studio: %s" % e)
            return {"CANCELLED"}

        self.report({"INFO"}, "Sent to Asset CUA Studio → %s" % out)
        return {"FINISHED"}


def _menu(self, context):
    self.layout.operator(ACUA_OT_export.bl_idname, icon="EXPORT")


_classes = (ACUA_Prefs, ACUA_OT_export)


def register():
    for c in _classes:
        bpy.utils.register_class(c)
    bpy.types.TOPBAR_MT_file_export.append(_menu)


def unregister():
    bpy.types.TOPBAR_MT_file_export.remove(_menu)
    for c in reversed(_classes):
        bpy.utils.unregister_class(c)


if __name__ == "__main__":
    register()
