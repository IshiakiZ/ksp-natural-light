"""
Natural Light's floodlight: a xenon searchlight of the kind that lit the Apollo and Shuttle pads by night (banks of them, 20
kilowatts each, round the pad): a round projector head of grey-painted cast aluminium, the faceted reflector and the short-arc
lamp seen through its front glass, cooling fins round its back, on a yoke over a pan bearing, on a square steel post with its
ballast box, bolted to a concrete footing. Made in Blender by this script alone (nothing downloaded, no model file kept by
hand: run it again to make everything again).

    /Applications/Blender.app/Contents/MacOS/Blender --background --factory-startup \\
        --python src/NaturalLight/Model/floodlight.py -- src/NaturalLight/PluginData [--quick] [--render FOLDER]

(always --background: Blender's window is never opened.) It writes into the folder given:

    floodlight.mesh          the meshes, as Natural Light reads them (see write_mesh): "stand" (footing, post, ballast box, yoke:
                             all that stays put once the lamp is turned to face its site) and "head" (the projector, which tilts
                             on the yoke: its housing, the reflector, the lamp and the glass as submeshes of their own), and a
                             marker, "glass", for where the light comes out
    floodlight_colour.png    paint, concrete, steel and the reflector's polish: colour with its shading (ambient occlusion) and a
                             little dirt in red, green and blue; how glossy each place is, in alpha
    floodlight_normal.png    the fine relief (the cast housing's grain, the box's louvres, the concrete's pores) as a
                             tangent-space normal map

--quick bakes with few samples, its pictures half as wide (for trying a change); --render draws pictures of the finished model
by day and lit, beside a kerbal-sized stand-in, into FOLDER.

The model's frame: x to the right, y forward (where the lamp points when level), z up, in metres; the ground at z = 0 and the
origin under the middle of the post. (Natural Light turns it into Unity's: x right, y up, z forward.) The head is given in a
frame of its own, level, its origin on the tilt axis 1.86 m up: Natural Light turns the stand to face its site and tilts the
head up on its yoke. Real sizes: the head 0.8 m across and 0.95 m long, its top 2.3 m up when level. No maker's or company's
name or mark anywhere.
"""
import bmesh
import bpy
import math
import os
import struct
import sys

import numpy as np
from mathutils import Matrix, Vector

ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = os.path.abspath(ARGS[0] if ARGS and not ARGS[0].startswith("--") else os.path.join(os.path.dirname(__file__), "..", "PluginData"))
QUICK = "--quick" in ARGS
RENDER = ARGS[ARGS.index("--render") + 1] if "--render" in ARGS else None

# ---------------------------------------------------------------- sizes (metres)
PIVOT_Z = 1.86                     # the tilt axis, above the ground
R = 0.36                           # the drum's radius
FRONT = 0.30                       # where the drum meets the bezel (head frame, along y)
GLASS_Y, GLASS_R = 0.342, 0.336    # the front glass
REFL_RIM_R, REFL_RIM_Y, REFL_DEPTH = 0.33, 0.29, 0.24
FOCAL = REFL_RIM_R ** 2 / (4 * REFL_DEPTH)                 # the reflector's focal length
REFL_VERTEX_Y = REFL_RIM_Y - REFL_DEPTH
FOCUS_Y = REFL_VERTEX_Y + FOCAL                            # where the arc burns
ARM_X = 0.47                       # the yoke's arms' inner faces, each side
BAKE_TILT = 50.0                   # the head's tilt when it is baked (about what the lamps are turned up to round a pad)
ATLAS = 1024 if QUICK else 2048
SAMPLES = 16 if QUICK else 128

PARTS = {"stand": [], "head": []}  # what goes into each exported mesh


# ---------------------------------------------------------------- materials
MATERIALS = {}


def node(tree, kind, **props):
    n = tree.nodes.new(kind)
    for k, v in props.items():
        setattr(n, k, v)
    return n


def material(name, colour, gloss, rough=0.5, build=None):
    """A material for baking: its base colour (with any pattern `build` adds) and how glossy it is (kept for the gloss bake).
    (Never metallic: a metal's diffuse colour bakes black, and the game's part shaders have no metalness anyway.)"""
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    t = m.node_tree
    bsdf = t.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*colour, 1.0)
    bsdf.inputs["Roughness"].default_value = rough
    if build is not None:
        build(t, bsdf)
    m["gloss"] = gloss
    MATERIALS[name] = m
    return m


def bump(t, bsdf, height, strength, distance):
    b = node(t, "ShaderNodeBump")
    b.inputs["Strength"].default_value = strength
    b.inputs["Distance"].default_value = distance
    t.links.new(height, b.inputs["Height"])
    t.links.new(b.outputs["Normal"], bsdf.inputs["Normal"])


def weathered(base, dusty, worn=None, grain=0.0, scale=3.0, amount=0.2, low=0.0, fine_scale=120.0):
    """Paint (or concrete, or steel) with blotchy dust over it, more of it low down if `low` (splashed up from the ground), lighter
    or darker at worn edges if `worn`, and a fine relief (a casting's grain, the pores of concrete) if `grain`."""
    def build(t, bsdf):
        coords = node(t, "ShaderNodeTexCoord")
        noise = node(t, "ShaderNodeTexNoise")
        noise.inputs["Scale"].default_value = scale
        noise.inputs["Detail"].default_value = 5.0
        t.links.new(coords.outputs["Object"], noise.inputs["Vector"])
        ramp = node(t, "ShaderNodeValToRGB")
        ramp.color_ramp.elements[0].position = 0.45
        ramp.color_ramp.elements[1].position = 0.75
        t.links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
        dirt = node(t, "ShaderNodeMath", operation="MULTIPLY")
        dirt.inputs[1].default_value = amount
        t.links.new(ramp.outputs["Color"], dirt.inputs[0])
        if low > 0:
            xyz = node(t, "ShaderNodeSeparateXYZ")
            t.links.new(coords.outputs["Object"], xyz.inputs[0])
            near = node(t, "ShaderNodeMapRange")
            near.inputs["From Min"].default_value = low
            near.inputs["From Max"].default_value = 0.0
            near.inputs["To Max"].default_value = 0.6
            t.links.new(xyz.outputs["Z"], near.inputs["Value"])
            more = node(t, "ShaderNodeMath", operation="MULTIPLY")
            t.links.new(near.outputs["Result"], more.inputs[0])
            t.links.new(noise.outputs["Fac"], more.inputs[1])
            both = node(t, "ShaderNodeMath", operation="MAXIMUM")
            t.links.new(dirt.outputs[0], both.inputs[0])
            t.links.new(more.outputs[0], both.inputs[1])
            dirt = both
        mix = node(t, "ShaderNodeMix", data_type="RGBA")
        mix.inputs[6].default_value = (*base, 1.0)
        mix.inputs[7].default_value = (*dusty, 1.0)
        t.links.new(dirt.outputs[0], mix.inputs[0])
        out = mix.outputs[2]
        if worn is not None:
            geo = node(t, "ShaderNodeNewGeometry")
            edge = node(t, "ShaderNodeMapRange")
            edge.inputs["From Min"].default_value = 0.53
            edge.inputs["From Max"].default_value = 0.62
            t.links.new(geo.outputs["Pointiness"], edge.inputs["Value"])
            speck = node(t, "ShaderNodeTexNoise")
            speck.inputs["Scale"].default_value = 45.0
            t.links.new(coords.outputs["Object"], speck.inputs["Vector"])
            k = node(t, "ShaderNodeMath", operation="MULTIPLY")
            t.links.new(edge.outputs["Result"], k.inputs[0])
            t.links.new(speck.outputs["Fac"], k.inputs[1])
            w = node(t, "ShaderNodeMix", data_type="RGBA")
            t.links.new(k.outputs[0], w.inputs[0])
            t.links.new(out, w.inputs[6])
            w.inputs[7].default_value = (*worn, 1.0)
            out = w.outputs[2]
        t.links.new(out, bsdf.inputs["Base Color"])
        if grain > 0:
            fine = node(t, "ShaderNodeTexNoise")
            fine.inputs["Scale"].default_value = fine_scale
            fine.inputs["Detail"].default_value = 3.0
            t.links.new(coords.outputs["Object"], fine.inputs["Vector"])
            bump(t, bsdf, fine.outputs["Fac"], grain, 0.002)
    return build


def louvres(t, bsdf):
    """The ballast box's side vents: slats across, 18 mm apart, each leaning out at its top (from the panel's own decal coordinates)."""
    uv = node(t, "ShaderNodeUVMap", uv_map="decal")
    sep = node(t, "ShaderNodeSeparateXYZ")
    t.links.new(uv.outputs["UV"], sep.inputs[0])
    scaled = node(t, "ShaderNodeMath", operation="MULTIPLY")
    scaled.inputs[1].default_value = 9.0                   # (slats up the panel)
    t.links.new(sep.outputs["Y"], scaled.inputs[0])
    saw = node(t, "ShaderNodeMath", operation="FRACT")
    t.links.new(scaled.outputs[0], saw.inputs[0])
    bump(t, bsdf, saw.outputs[0], 1.0, 0.01)
    dark = node(t, "ShaderNodeMix", data_type="RGBA")
    dark.inputs[6].default_value = (0.06, 0.06, 0.065, 1.0)
    dark.inputs[7].default_value = (0.4, 0.41, 0.42, 1.0)
    t.links.new(saw.outputs[0], dark.inputs[0])
    t.links.new(dark.outputs[2], bsdf.inputs["Base Color"])


def decal_image(name, w, h, draw):
    img = bpy.data.images.new(name, w, h, alpha=False)
    img.colorspace_settings.name = "sRGB"
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32) + 0.5
    rgb = draw(xx / w, yy / h)
    img.pixels.foreach_set(np.concatenate([rgb, np.ones((h, w, 1))], axis=2).astype(np.float32).ravel())
    img.pack()
    return img


def warning(u, v):
    """A high-voltage warning: a yellow triangle edged in black with a black lightning bolt in it (no words), on the box's grey."""
    grey = np.array([0.4, 0.41, 0.42], np.float32)
    yellow = np.array([0.88, 0.66, 0.04], np.float32)
    x, y = u - 0.5, v - 0.08                                # (the triangle's base along the bottom)
    inside = (y > 0) & (y < 0.84) & (np.abs(x) < 0.5 * (1 - y / 0.84))
    inner = (y > 0.07) & (y < 0.74) & (np.abs(x) < 0.5 * (1 - y / 0.84) - 0.07)
    bolt = np.zeros_like(u, bool)
    for (ax, ay, bx, by) in ((0.06, 0.62, -0.06, 0.36), (-0.06, 0.36, 0.05, 0.38), (0.05, 0.38, -0.05, 0.12)):
        dx, dy = bx - ax, by - ay
        k = np.clip(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0, 1)
        bolt |= np.hypot(x - ax - dx * k, y - ay - dy * k) < 0.03
    rgb = np.broadcast_to(grey, u.shape + (3,)).copy()
    rgb[inside] = 0.03
    rgb[inner] = yellow
    rgb[inner & bolt] = 0.03
    return rgb


def rating(u, v):
    """The ballast's rating plate: brushed steel with rows of fine dark marks where its stamped lines are, and its four rivets."""
    rgb = np.empty(u.shape + (3,), np.float32)
    rgb[...] = 0.66 + 0.04 * np.sin(v * 400.0)[..., None] * 0.2
    rows = (np.abs(((v - 0.12) * 7.0) % 1.0 - 0.5) < 0.12) & (v > 0.1) & (v < 0.8) & (u > 0.08) & (u < 0.92)
    rng = np.random.default_rng(7)
    marks = rng.random((64, 64)) > 0.45
    mx = marks[np.clip((v * 64).astype(int), 0, 63), np.clip((u * 64).astype(int), 0, 63)]
    rgb[rows & mx] = 0.12
    for cx, cy in ((0.05, 0.1), (0.95, 0.1), (0.05, 0.9), (0.95, 0.9)):
        rgb[np.hypot((u - cx) * 2.0, v - cy) < 0.05] = 0.4
    return rgb


def image_material(name, img, gloss):
    def build(t, bsdf):
        uv = node(t, "ShaderNodeUVMap", uv_map="decal")
        tex = node(t, "ShaderNodeTexImage")
        tex.image = img
        t.links.new(uv.outputs["UV"], tex.inputs["Vector"])
        t.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    return material(name, (0.5, 0.5, 0.5), gloss, build=build)


def make_materials():
    grey = (0.33, 0.345, 0.36)                             # grey paint on cast aluminium
    material("paint", grey, 0.42, build=weathered(grey, (0.42, 0.4, 0.36), worn=(0.6, 0.61, 0.62), grain=0.3, amount=0.22))
    box = (0.4, 0.41, 0.42)
    material("box", box, 0.4, build=weathered(box, (0.56, 0.54, 0.5), worn=(0.63, 0.64, 0.65), amount=0.25, low=0.9))
    conc = (0.56, 0.55, 0.52)
    material("concrete", conc, 0.06, rough=0.9, build=weathered(conc, (0.42, 0.4, 0.36), grain=1.0, scale=4.0, amount=0.35, low=0.15, fine_scale=45.0))
    galv = (0.6, 0.61, 0.62)
    material("galv", galv, 0.45, build=weathered(galv, (0.5, 0.49, 0.46), scale=30.0, amount=0.3))
    material("steel", (0.36, 0.37, 0.38), 0.55, build=weathered((0.36, 0.37, 0.38), (0.3, 0.26, 0.2), amount=0.25, scale=20.0))
    material("rubber", (0.03, 0.03, 0.032), 0.14, rough=0.8)
    material("louvre", box, 0.35, build=louvres)
    image_material("warning", decal_image("warning", 256, 256, warning), 0.5)
    image_material("rating", decal_image("rating", 256, 128, rating), 0.65)
    material("reflector", (0.86, 0.87, 0.88), 0.95, rough=0.1)
    material("socket", (0.22, 0.22, 0.23), 0.6)
    material("quartz", (0.9, 0.92, 0.95), 0.95, rough=0.05)
    material("electrode", (0.3, 0.3, 0.31), 0.6)
    material("glass", (0.6, 0.65, 0.7), 0.95, rough=0.05)


# ---------------------------------------------------------------- shapes
def finish(name, bm, mat, into, bevel=0.0, segments=3, angle=30.0, decal=None, smooth=False):
    """Make an object of a bmesh, with one material, an optional rounding of its edges, and its decal coordinates."""
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    obj = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(obj)
    me.materials.append(MATERIALS[mat])
    if "decal" not in me.uv_layers:
        me.uv_layers.new(name="decal")
    if decal is not None:
        uv = me.uv_layers["decal"]
        for poly in me.polygons:
            for li in poly.loop_indices:
                uv.data[li].uv = decal(me.vertices[me.loops[li].vertex_index].co)
    if smooth:
        for p in me.polygons:
            p.use_smooth = True
    if bevel > 0:
        b = obj.modifiers.new("Bevel", "BEVEL")
        b.width = bevel
        b.segments = segments
        b.limit_method = "ANGLE"
        b.angle_limit = math.radians(angle)
        b.use_clamp_overlap = True
    if into is not None:
        PARTS[into].append(obj)
    return obj


def orient(bm, ref, away=True):
    """Every face turned to face away from (or, `away` False, towards) the point `ref`: for open surfaces, whose way out
    recalc_face_normals cannot know."""
    bm.normal_update()
    ref = Vector(ref)
    for f in bm.faces:
        if (f.normal.dot(f.calc_center_median() - ref) < 0) == away:
            f.normal_flip()
    bm.normal_update()


def box(name, lo, hi, mat="paint", bevel=0.0, into="stand", segments=3, decal=None):
    lo, hi = Vector(lo), Vector(hi)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    size, mid = hi - lo, (hi + lo) * 0.5
    for v in bm.verts:
        v.co = Vector((v.co.x * size.x, v.co.y * size.y, v.co.z * size.z)) + mid
    return finish(name, bm, mat, into, bevel=min(bevel, 0.49 * min(size)), segments=segments, decal=decal, smooth=bevel > 0)


def cylinder(name, a, b, r, mat="paint", segments=24, into="stand", caps=True, smooth=True):
    a, b = Vector(a), Vector(b)
    axis = b - a
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=caps, cap_tris=False, segments=segments, radius1=r, radius2=r, depth=axis.length)
    rot = axis.normalized().to_track_quat("Z", "Y").to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=Matrix.Translation((a + b) * 0.5) @ rot, verts=bm.verts)
    return finish(name, bm, mat, into, smooth=smooth and segments > 8)


def sphere(name, c, r, mat="paint", into="stand", segs=12, scale=(1, 1, 1)):
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=segs, v_segments=max(4, segs // 2), radius=r)
    for v in bm.verts:
        v.co = Vector((v.co.x * scale[0], v.co.y * scale[1], v.co.z * scale[2])) + Vector(c)
    return finish(name, bm, mat, into, smooth=True)


def tube(name, points, r, mat="rubber", into="stand", segments=10):
    """A bent tube (a cable, a handle) through the points: a straight tube between each two, a ball at each bend."""
    for i in range(len(points) - 1):
        cylinder(f"{name}.{i}", points[i], points[i + 1], r, mat, segments, into, caps=(i == 0 or i == len(points) - 2))
    for i in range(1, len(points) - 1):
        sphere(f"{name}.j{i}", points[i], r, mat, into, segs=segments)


def lathe(name, profile, mat="paint", into="head", axis="y", centre=(0, 0, 0), segments=48, closed=False, smooth=True,
          inside=None, toward=None, bevel=0.0):
    """A solid of revolution: `profile` a list of (r, a) points (r out from the axis, a along it) turned once round the axis
    through `centre`. A closed profile makes a closed solid (its faces turned outwards); an open one a surface, its faces turned
    away from the point `inside` or towards the point `toward`."""
    c = Vector(centre)

    def place(r, a, ang):
        ca, sa = math.cos(ang), math.sin(ang)
        if axis == "y":
            return c + Vector((r * ca, a, r * sa))
        if axis == "z":
            return c + Vector((r * ca, r * sa, a))
        return c + Vector((a, r * ca, r * sa))
    bm = bmesh.new()
    cols = []
    for (r, a) in profile:
        if r < 1e-6:
            v = bm.verts.new(place(0.0, a, 0.0))
            cols.append([v] * segments)
        else:
            cols.append([bm.verts.new(place(r, a, 2 * math.pi * k / segments)) for k in range(segments)])
    n = len(profile)
    for i in range(n if closed else n - 1):
        j = (i + 1) % n
        for k in range(segments):
            k2 = (k + 1) % segments
            quad = [cols[i][k], cols[i][k2], cols[j][k2], cols[j][k]]
            seen = []
            for v in quad:
                if v not in seen:
                    seen.append(v)
            if len(seen) >= 3:
                bm.faces.new(seen)
    if closed:
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    elif toward is not None:
        orient(bm, toward, away=False)
    elif inside is not None:
        orient(bm, inside, away=True)
    return finish(name, bm, mat, into, smooth=smooth, bevel=bevel)


def prism(name, outline, x0, x1, mat="paint", bevel=0.0, into="stand", segments=3, angle=30.0):
    """A solid with a side view `outline` (points (y, z), in order round it) the same all the way from x0 to x1."""
    bm = bmesh.new()
    left = [bm.verts.new((x0, y, z)) for (y, z) in outline]
    right = [bm.verts.new((x1, y, z)) for (y, z) in outline]
    bm.faces.new(left)
    bm.faces.new(list(reversed(right)))
    n = len(outline)
    for i in range(n):
        bm.faces.new((left[i], left[(i + 1) % n], right[(i + 1) % n], right[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return finish(name, bm, mat, into, bevel=bevel, segments=segments, angle=angle, smooth=bevel > 0)


def plate(name, points, thickness, mat="paint", into="stand", bevel=0.0):
    """A flat plate of any outline: `points` its corners (3-d, in order, all in one plane), `thickness` thick out of that plane."""
    bm = bmesh.new()
    pts = [Vector(p) for p in points]
    n = (pts[1] - pts[0]).cross(pts[2] - pts[0]).normalized()
    a = [bm.verts.new(p - n * thickness / 2) for p in pts]
    b = [bm.verts.new(p + n * thickness / 2) for p in pts]
    bm.faces.new(a)
    bm.faces.new(list(reversed(b)))
    for i in range(len(pts)):
        j = (i + 1) % len(pts)
        bm.faces.new((a[i], a[j], b[j], b[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return finish(name, bm, mat, into, bevel=bevel)


def panel(name, centre, normal, w, h, mat, into="stand", up=(0, 0, 1)):
    """A thin flat rectangle w by h (2 mm proud), facing `normal`, its decal coordinates covering it once (u across, v up)."""
    n = Vector(normal).normalized()
    v_ax = Vector(up) - n * n.dot(Vector(up))
    v_ax.normalize()
    u_ax = v_ax.cross(n)
    c = Vector(centre)
    corners = [c - u_ax * w / 2 - v_ax * h / 2, c + u_ax * w / 2 - v_ax * h / 2, c + u_ax * w / 2 + v_ax * h / 2, c - u_ax * w / 2 + v_ax * h / 2]
    bm = bmesh.new()
    front = [bm.verts.new(p + n * 0.002) for p in corners]
    back = [bm.verts.new(p) for p in corners]
    bm.faces.new(front)
    bm.faces.new(list(reversed(back)))
    for i in range(4):
        j = (i + 1) % 4
        bm.faces.new((front[i], back[i], back[j], front[j]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    origin = corners[0]

    def decal(p):
        d = Vector(p) - origin
        return (d.dot(u_ax) / w, d.dot(v_ax) / h)
    return finish(name, bm, mat, into, decal=decal)


def nut(name, c, axis, r, h, mat="steel", into="stand"):
    """A hex nut (or a bolt's head) of across-corners radius r, h tall, on `axis` from `c`."""
    a = Vector(c)
    return cylinder(name, a, a + Vector(axis).normalized() * h, r, mat, segments=6, into=into, smooth=False)


# ---------------------------------------------------------------- the stand
def build_stand():
    # the concrete footing, its top a little above the ground and its body sunk well into it (so it sits on a slope too)
    box("footing", (-0.55, -0.55, -0.45), (0.55, 0.55, 0.12), "concrete", bevel=0.025)
    # the base plate, bolted down at its corners, the post welded to it with a gusset each side
    box("base plate", (-0.23, -0.23, 0.12), (0.23, 0.23, 0.145), "galv", bevel=0.004)
    for sx in (-1, 1):
        for sy in (-1, 1):
            x, y = 0.18 * sx, 0.18 * sy
            cylinder(f"anchor {sx}{sy}", (x, y, 0.145), (x, y, 0.2), 0.012, "galv", segments=10)
            cylinder(f"washer {sx}{sy}", (x, y, 0.145), (x, y, 0.151), 0.026, "galv", segments=16)
            nut(f"nut {sx}{sy}", (x, y, 0.151), (0, 0, 1), 0.022, 0.022, "galv")
    box("post", (-0.1, -0.1, 0.145), (0.1, 0.1, 1.02), "paint", bevel=0.008)
    for (dx, dy) in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        out = Vector((dx, dy, 0))
        base = out * 0.1
        plate(f"gusset {dx}{dy}", [base + Vector((0, 0, 0.145)), base + out * 0.11 + Vector((0, 0, 0.145)), base + Vector((0, 0, 0.29))], 0.012, "paint")
    box("top plate", (-0.15, -0.15, 1.02), (0.15, 0.15, 1.042), "paint", bevel=0.004)
    # the pan bearing, its ring of bolts, and the handle that locks it
    lathe("pan bearing", [(0.0, 1.042), (0.13, 1.042), (0.13, 1.09), (0.122, 1.1), (0.0, 1.1)], "paint", "stand", axis="z", closed=True)
    for k in range(8):
        a = 2 * math.pi * (k + 0.5) / 8
        nut(f"bearing bolt {k}", (0.106 * math.cos(a), 0.106 * math.sin(a), 1.1), (0, 0, 1), 0.012, 0.01)
    cylinder("pan lock", (0.12, -0.04, 1.07), (0.2, -0.07, 1.07), 0.011, "steel", segments=10)
    sphere("pan lock knob", (0.205, -0.072, 1.07), 0.02, "rubber", segs=12)
    # the yoke: a beam across, an arm up each side to the tilt axis, rounded over it
    box("yoke beam", (-0.485, -0.075, 1.1), (0.485, 0.075, 1.17), "paint", bevel=0.008)
    top = [(0.06 * math.cos(math.pi * k / 10), PIVOT_Z + 0.06 * math.sin(math.pi * k / 10)) for k in range(11)]
    outline = [(-0.06, 1.1), (0.06, 1.1)] + top
    for side in (-1, 1):
        x0, x1 = (ARM_X, ARM_X + 0.03) if side > 0 else (-ARM_X - 0.03, -ARM_X)
        prism(f"arm {side:+d}", outline, x0, x1, "paint", bevel=0.004)
        xin = ARM_X * side
        plate(f"arm gusset {side:+d}", [Vector((xin, 0, 1.17)), Vector((xin - 0.1 * side, 0, 1.17)), Vector((xin, 0, 1.29))], 0.014, "paint")
        # the hub on the outside of each arm where the head's trunnion turns
        xo = (ARM_X + 0.03) * side
        cylinder(f"hub {side:+d}", (xo, 0, PIVOT_Z), (xo + 0.03 * side, 0, PIVOT_Z), 0.07, "paint", segments=28)
        for k in range(6):
            a = 2 * math.pi * k / 6
            nut(f"hub bolt {side:+d} {k}", (xo + 0.03 * side, 0.05 * math.cos(a), PIVOT_Z + 0.05 * math.sin(a)), (side, 0, 0), 0.009, 0.008)
    # the tilt lock on the right: a hand wheel; and a scale on the left, with its pointer
    xw = ARM_X + 0.06
    cylinder("tilt wheel boss", (xw, 0, PIVOT_Z), (xw + 0.05, 0, PIVOT_Z), 0.022, "steel", segments=16)
    lathe("tilt wheel rim", [(0.07, -0.007), (0.077, 0.0), (0.07, 0.007), (0.063, 0.0)], "rubber", "stand", axis="x", centre=(xw + 0.045, 0, PIVOT_Z), segments=32, closed=True)
    for k in range(3):
        a = 2 * math.pi * k / 3 + 0.3
        cylinder(f"tilt wheel spoke {k}", (xw + 0.045, 0, PIVOT_Z), (xw + 0.045, 0.066 * math.cos(a), PIVOT_Z + 0.066 * math.sin(a)), 0.006, "steel", segments=8)
    plate("tilt scale", [Vector((-ARM_X - 0.032, 0.0, PIVOT_Z))] + [Vector((-ARM_X - 0.032, 0.115 * math.cos(math.radians(a)), PIVOT_Z + 0.115 * math.sin(math.radians(a)))) for a in range(-100, -1, 15)],
          0.003, "galv")
    # the ballast box on the back of the post: door, hinges, latch, louvres each side, rating plate and warning
    box("ballast box", (-0.2, -0.3, 0.32), (0.2, -0.1, 0.84), "box", bevel=0.01)
    box("ballast door", (-0.185, -0.304, 0.335), (0.185, -0.299, 0.825), "box", bevel=0.003)
    for z in (0.4, 0.76):
        cylinder(f"hinge {z}", (-0.19, -0.306, z), (-0.19, -0.306, z + 0.06), 0.009, "galv", segments=10)
    box("latch", (0.13, -0.312, 0.54), (0.16, -0.304, 0.62), "steel", bevel=0.002)
    cylinder("latch handle", (0.145, -0.312, 0.6), (0.145, -0.34, 0.6), 0.007, "steel", segments=8)
    for side in (-1, 1):
        panel(f"louvres {side:+d}", (0.2 * side, -0.2, 0.7), (side, 0, 0), 0.15, 0.18, "louvre")
        panel(f"lower louvres {side:+d}", (0.2 * side, -0.2, 0.46), (side, 0, 0), 0.15, 0.1, "louvre")
    panel("rating plate", (0.0, -0.304, 0.73), (0, -1, 0), 0.13, 0.065, "rating")
    panel("warning", (0.0, -0.304, 0.6), (0, -1, 0), 0.11, 0.11, "warning")
    for z in (0.42, 0.74):
        box(f"strap {z}", (-0.105, -0.105, z), (0.105, 0.105, z + 0.03), "galv", bevel=0.003)
    # the conduit from the box down into the footing, and the lamp's cable up the post and the yoke to the right hub
    cylinder("conduit", (0.1, -0.2, 0.12), (0.1, -0.2, 0.32), 0.018, "galv", segments=14)
    cylinder("conduit fitting", (0.1, -0.2, 0.29), (0.1, -0.2, 0.33), 0.025, "galv", segments=6, smooth=False)
    cylinder("cable gland", (0.12, -0.22, 0.84), (0.12, -0.22, 0.87), 0.018, "steel", segments=6, smooth=False)
    tube("cable", [(0.12, -0.22, 0.86), (0.12, -0.22, 0.95), (0.12, -0.175, 1.0), (0.12, -0.175, 1.14), (0.14, -0.1, 1.19),
                   (0.43, -0.095, 1.19), (0.462, -0.08, 1.24), (0.462, -0.08, 1.7), (0.462, -0.06, 1.76)], 0.011, "rubber")
    for z in (1.4, 1.6):
        box(f"cable clip {z}", (0.448, -0.094, z), (0.47, -0.066, z + 0.02), "galv")


# ---------------------------------------------------------------- the head (in its own frame: the tilt axis at the origin)
def build_head():
    # the drum: its outer wall, the taper behind the fins, and the domed cap at the back
    lathe("drum", [(R, FRONT + 0.004), (R, -0.34), (0.315, -0.395), (0.285, -0.425), (0.21, -0.452), (0.12, -0.464), (0.0, -0.468)],
          "paint", inside=(0, -0.1, 0))
    # the bezel round the glass, its gasket, its ring of clamp bolts, and the glass
    lathe("bezel", [(0.335, FRONT - 0.002), (0.335, 0.351), (0.345, 0.358), (0.388, 0.358), (0.397, 0.35), (0.397, FRONT), (R - 0.002, FRONT - 0.004)],
          "paint", closed=True)
    lathe("gasket", [(0.328, GLASS_Y - 0.004), (0.3365, GLASS_Y - 0.004), (0.3365, GLASS_Y + 0.006), (0.328, GLASS_Y + 0.006)], "rubber", segments=40, closed=True)
    for k in range(12):
        a = 2 * math.pi * (k + 0.5) / 12
        nut(f"bezel bolt {k}", (0.368 * math.cos(a), 0.357, 0.368 * math.sin(a)), (0, 1, 0), 0.0105, 0.009, "steel", "head")
    bm = bmesh.new()
    rim = [bm.verts.new((GLASS_R * math.cos(2 * math.pi * k / 48), GLASS_Y, GLASS_R * math.sin(2 * math.pi * k / 48))) for k in range(48)]
    mid = bm.verts.new((0, GLASS_Y, 0))
    for k in range(48):
        bm.faces.new((mid, rim[(k + 1) % 48], rim[k]))
    orient(bm, (0, 0, 0), away=True)
    finish("glass", bm, "glass", "head", smooth=True)
    # the reflector: a deep paraboloid of polished facets, the lamp's socket through its middle
    rings = [0.036, 0.075, 0.115, 0.158, 0.2, 0.243, 0.287, REFL_RIM_R]
    bm = bmesh.new()
    cols = []
    for i, r in enumerate(rings):
        twist = (math.pi / 24) * (i % 2)                    # (each ring's facets set half a facet round from the last's)
        y = REFL_VERTEX_Y + r * r / (4 * FOCAL)
        cols.append([bm.verts.new((r * math.cos(2 * math.pi * k / 24 + twist), y, r * math.sin(2 * math.pi * k / 24 + twist))) for k in range(24)])
    for i in range(len(rings) - 1):
        for k in range(24):
            k2 = (k + 1) % 24
            if i % 2 == 0:
                bm.faces.new((cols[i][k], cols[i][k2], cols[i + 1][k]))
                bm.faces.new((cols[i][k2], cols[i + 1][k2], cols[i + 1][k]))
            else:
                bm.faces.new((cols[i][k], cols[i][k2], cols[i + 1][k2]))
                bm.faces.new((cols[i][k], cols[i + 1][k2], cols[i + 1][k]))
    orient(bm, (0, FOCUS_Y, 0), away=False)
    finish("reflector", bm, "reflector", "head", smooth=False)
    # (a ring closing the gap between the reflector's rim and the bezel)
    lathe("reflector lip", [(REFL_RIM_R - 0.002, REFL_RIM_Y), (0.3365, REFL_RIM_Y + 0.004), (0.3365, GLASS_Y - 0.004)], "reflector", segments=48, toward=(0, FOCUS_Y, 0))
    # the lamp: its socket, the cathode's stem, the quartz bulb with the arc's gap at the focus, the anode, held by a spider of three
    cylinder("socket", (0, REFL_VERTEX_Y - 0.06, 0), (0, REFL_VERTEX_Y + 0.025, 0), 0.034, "socket", segments=20, into="head")
    cylinder("cathode stem", (0, REFL_VERTEX_Y + 0.02, 0), (0, FOCUS_Y - 0.035, 0), 0.0095, "electrode", segments=12, into="head")
    sphere("bulb", (0, FOCUS_Y, 0), 1.0, "quartz", "head", segs=16, scale=(0.027, 0.044, 0.027))
    cylinder("anode stem", (0, FOCUS_Y + 0.035, 0), (0, FOCUS_Y + 0.095, 0), 0.0115, "electrode", segments=12, into="head")
    cylinder("anode cap", (0, FOCUS_Y + 0.09, 0), (0, FOCUS_Y + 0.105, 0), 0.017, "electrode", segments=12, into="head")
    for k in range(3):
        a = math.pi / 2 + 2 * math.pi * k / 3
        tip = Vector((0, FOCUS_Y + 0.1, 0))
        rim_pt = Vector((0.318 * math.cos(a), REFL_RIM_Y - 0.004, 0.318 * math.sin(a)))
        cylinder(f"spider {k}", tip, rim_pt, 0.0025, "socket", segments=6, into="head", smooth=False)
    # cooling fins round the back of the drum, tied by four ribs
    for i in range(7):
        y0 = -0.33 + i * 0.038
        lathe(f"fin {i}", [(R - 0.003, y0), (0.4, y0 + 0.0012), (0.404, y0 + 0.004), (0.4, y0 + 0.0068), (R - 0.003, y0 + 0.008)], "paint", segments=40, closed=True)
    for k in range(4):
        a = math.pi / 4 + k * math.pi / 2
        bm = bmesh.new()
        bmesh.ops.create_cube(bm, size=1.0)
        for v in bm.verts:
            v.co = Vector((v.co.x * 0.045 + (R + 0.0195), v.co.y * 0.26 - 0.2, v.co.z * 0.01))
        bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(-a, 3, "Y"))
        finish(f"rib {k}", bm, "paint", "head")
    # the trunnions it tilts on, each from a boss cast on the drum's side
    for side in (-1, 1):
        cylinder(f"boss {side:+d}", (0.33 * side, 0, 0), (0.378 * side, 0, 0), 0.085, "paint", segments=28, into="head")
        cylinder(f"trunnion {side:+d}", (0.378 * side, 0, 0), ((ARM_X - 0.002) * side, 0, 0), 0.05, "steel", segments=20, into="head")
    # handles at the back, to aim it by
    for side in (-1, 1):
        x = 0.2 * side
        tube(f"handle {side:+d}", [(x, -0.4, 0.13), (x, -0.53, 0.13), (x, -0.53, -0.13), (x, -0.4, -0.13)], 0.0125, "steel", "head", segments=10)


# ---------------------------------------------------------------- putting together, unwrapping, baking
def apply_modifiers(obj):
    deps = bpy.context.evaluated_depsgraph_get()
    ev = obj.evaluated_get(deps)
    me = bpy.data.meshes.new_from_object(ev, preserve_all_data_layers=True, depsgraph=deps)
    old = obj.data
    obj.modifiers.clear()
    obj.data = me
    bpy.data.meshes.remove(old)


def join(name, objs):
    for o in objs:
        apply_modifiers(o)
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    if len(objs) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    obj.name = name
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bm.to_mesh(me)
    bm.free()
    # (smooth where a piece was made smooth, sharp at creases over 46 degrees; the reflector's facets stay flat)
    me.set_sharp_from_angle(angle=math.radians(46))
    return obj


def unwrap(objs, margin):
    for o in objs:
        if "atlas" not in o.data.uv_layers:
            o.data.uv_layers.new(name="atlas")
        o.data.uv_layers.active = o.data.uv_layers["atlas"]
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=margin, area_weight=0.0, correct_aspect=True, scale_to_bounds=False)
    # (packed again by their real outlines: the fins' rings and the bezel's are wide rings, and the small pieces go in their middles)
    bpy.ops.uv.select_all(action="SELECT")
    bpy.ops.uv.pack_islands(udim_source="CLOSEST_UDIM", rotate=True, margin=margin, shape_method="CONCAVE")
    bpy.ops.object.mode_set(mode="OBJECT")


def image(name, size, colour=True):
    img = bpy.data.images.new(name, size, size, alpha=True)
    img.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    return img


def target(objs, img):
    """Make `img` what every material of these objects is baked into."""
    for o in objs:
        for m in o.data.materials:
            t = m.node_tree
            n = t.nodes.get("bake target") or t.nodes.new("ShaderNodeTexImage")
            n.name = "bake target"
            n.image = img
            t.nodes.active = n


def bake(objs, kind, img, samples=4, **kw):
    """Bake into `img` (through each object's "atlas" coordinates), with this many samples a texel."""
    target(objs, img)
    for o in objs:
        layer = o.data.uv_layers["atlas"]
        o.data.uv_layers.active = layer
        layer.active_render = True
    bpy.context.scene.cycles.samples = samples
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.bake(type=kind, margin=12, use_clear=True, **kw)     # (wide margins: thin parts keep their own colour in the smaller sizes)
    print("baked", kind, "into", img.name)


def pixels(img):
    a = np.empty(img.size[0] * img.size[1] * 4, np.float32)
    img.pixels.foreach_get(a)
    return a.reshape(img.size[1], img.size[0], 4)


def save(arr, name, colour=True):
    h, w = arr.shape[:2]
    img = bpy.data.images.new(name, w, h, alpha=True)
    img.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    img.pixels.foreach_set(np.ascontiguousarray(arr, np.float32).ravel())
    img.filepath_raw = os.path.join(OUT, name)
    img.file_format = "PNG"
    img.save()
    print("wrote", img.filepath_raw)


def swap(objs, value_of):
    """For the bakes after the colour's: each material's base colour becomes a number of its own (for good: the colours are baked
    already)."""
    for m in {m for o in objs for m in o.data.materials}:
        sock = m.node_tree.nodes.get("Principled BSDF").inputs["Base Color"]
        for l in list(sock.links):
            m.node_tree.links.remove(l)
        g = float(value_of(m))
        sock.default_value = (g, g, g, 1.0)


SHINY = {"reflector", "quartz", "glass", "socket", "electrode"}       # (inside the lamp, where the shading of the drum is not wanted)


def compose(colour, ao, gloss, shiny, name, dirt=(0.36, 0.33, 0.28)):
    """The paint as the game shows it: its colour darkened in the folds by the ambient occlusion and a little dirt gathered there
    (not inside the lamp, which is polished and lit from the front), and its gloss in alpha."""
    c, a, g, s = pixels(colour), pixels(ao)[..., :1], pixels(gloss)[..., :1], pixels(shiny)[..., :1]
    # (the occlusion a little blurred, to take the speckle of its sampling out: the islands' margins take the overspill)
    k = np.pad(a, ((1, 1), (1, 1), (0, 0)), mode="edge")
    a = sum(k[1 + dy:k.shape[0] - 1 + dy, 1 + dx:k.shape[1] - 1 + dx] for dy in (-1, 0, 1) for dx in (-1, 0, 1)) / 9.0
    a = a * (1 - s) + 0.85 * s + 0.15 * a * s
    shade = 0.3 + 0.7 * np.power(np.clip(a, 0, 1), 1.1)
    rgb = c[..., :3] * shade
    rgb = rgb * (1 - 0.2 * (1 - a)) + np.array(dirt, np.float32) * 0.2 * (1 - a) * shade
    alpha = np.clip(g * (0.6 + 0.4 * a), 0, 1)
    save(np.concatenate([np.clip(rgb, 0, 1), alpha], axis=2), name)


# ---------------------------------------------------------------- the mesh file
EXPORT = {"reflector": "reflector", "socket": "reflector", "electrode": "reflector", "quartz": "bulb", "glass": "glass"}


def write_mesh(pieces, markers, path):
    """
    The meshes as Natural Light reads them (little-endian):
        "NLMESH01", u32 how many pieces; for each: name (u16 length, utf-8), where its own frame is in its parent's (position, 3
        f32, and rotation, a quaternion x y z w, 4 f32), u32 vertices, then for each vertex position (3 f32), normal (3 f32),
        tangent (4 f32), uv (2 f32); u32 how many submeshes; for each: material (u16 length, utf-8), u32 indices, the indices
        (u32). A marker is a piece with no vertices and no submeshes: only a place, in the frame of the piece it is named for.
    In Unity's axes (x right, y up, z forward: Blender's y and z swapped, which mirrors, so every triangle is turned round and the
    tangents' bitangent sign changes) and metres. `pieces`: name -> (object, where its frame is, in Blender's axes); the object's
    mesh is in its own frame already.
    """
    def unity(v):
        return np.array((v[0], v[2], v[1]), np.float64)

    out = bytearray(b"NLMESH01")
    out += struct.pack("<I", len(pieces) + len(markers))
    for name, (obj, frame) in pieces.items():
        me = obj.data
        me.calc_tangents(uvmap="atlas")
        me.calc_loop_triangles()
        uv = me.uv_layers["atlas"]
        normals = me.corner_normals
        index, verts, subs = {}, [], {}
        for tri in me.loop_triangles:
            mat = me.materials[tri.material_index].name if me.materials else "paint"
            key_mat = EXPORT.get(mat, "paint")
            ids = []
            for li in tri.loops:
                loop = me.loops[li]
                P, N, T = unity(me.vertices[loop.vertex_index].co), unity(normals[li].vector), unity(loop.tangent)
                u = uv.data[li].uv
                key = (round(P[0], 5), round(P[1], 5), round(P[2], 5), round(N[0], 3), round(N[1], 3), round(N[2], 3), round(u.x, 5), round(u.y, 5), key_mat)
                if key not in index:
                    index[key] = len(verts)
                    verts.append((*P, *N, *T, -loop.bitangent_sign, u.x, u.y))
                ids.append(index[key])
            subs.setdefault(key_mat, []).extend((ids[0], ids[2], ids[1]))
        nb = name.encode()
        out += struct.pack("<H", len(nb)) + nb
        out += struct.pack("<7f", *unity(frame), 0.0, 0.0, 0.0, 1.0)
        out += struct.pack("<I", len(verts))
        for v in verts:
            out += struct.pack("<12f", *v)
        out += struct.pack("<I", len(subs))
        for mat, ids in subs.items():
            mb = mat.encode()
            out += struct.pack("<H", len(mb)) + mb + struct.pack("<I", len(ids)) + struct.pack(f"<{len(ids)}I", *ids)
        print(f"mesh {name}: {len(verts)} vertices, {sum(len(i) for i in subs.values()) // 3} triangles, submeshes {list(subs)}")
    for name, at in markers.items():
        nb = name.encode()
        out += struct.pack("<H", len(nb)) + nb + struct.pack("<7f", *unity(at), 0.0, 0.0, 0.0, 1.0) + struct.pack("<II", 0, 0)
    with open(path, "wb") as f:
        f.write(out)
    print("wrote", path, len(out), "bytes")


# ---------------------------------------------------------------- pictures of the result, for checking
def render(folder, stand, head):
    os.makedirs(folder, exist_ok=True)
    scene = bpy.context.scene

    def shown(name, emit=0.0, alpha=None):
        m = bpy.data.materials.new(name)
        m.use_nodes = True
        t = m.node_tree
        bsdf = t.nodes.get("Principled BSDF")
        uv = node(t, "ShaderNodeUVMap", uv_map="atlas")
        tex = node(t, "ShaderNodeTexImage")
        tex.image = bpy.data.images.load(os.path.join(OUT, "floodlight_colour.png"))
        t.links.new(uv.outputs["UV"], tex.inputs["Vector"])
        t.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        inv = node(t, "ShaderNodeMath", operation="SUBTRACT")
        inv.inputs[0].default_value = 1.0
        t.links.new(tex.outputs["Alpha"], inv.inputs[1])
        rough = node(t, "ShaderNodeMapRange")
        rough.inputs["To Min"].default_value = 0.12
        rough.inputs["To Max"].default_value = 0.9
        t.links.new(inv.outputs[0], rough.inputs["Value"])
        t.links.new(rough.outputs["Result"], bsdf.inputs["Roughness"])
        nt = node(t, "ShaderNodeTexImage")
        nt.image = bpy.data.images.load(os.path.join(OUT, "floodlight_normal.png"))
        nt.image.colorspace_settings.name = "Non-Color"
        t.links.new(uv.outputs["UV"], nt.inputs["Vector"])
        nm = node(t, "ShaderNodeNormalMap", uv_map="atlas")
        t.links.new(nt.outputs["Color"], nm.inputs["Color"])
        t.links.new(nm.outputs["Normal"], bsdf.inputs["Normal"])
        if emit > 0:
            t.links.new(tex.outputs["Color"], bsdf.inputs["Emission Color"])
            bsdf.inputs["Emission Strength"].default_value = emit
        if alpha is not None:
            bsdf.inputs["Alpha"].default_value = alpha
            bsdf.inputs["Base Color"].default_value = (0.6, 0.65, 0.7, 1)
            for l in list(bsdf.inputs["Base Color"].links):
                t.links.remove(l)
        return m

    paint, lit, glass = shown("shown paint"), shown("shown lit", emit=12.0), shown("shown glass", alpha=0.15)
    lit_slots = [i for i, m in enumerate(head.data.materials) if m.name.startswith(("reflector", "quartz", "socket", "electrode"))]
    for o in (stand, head):
        for i, m in enumerate(o.data.materials):
            o.data.materials[i] = glass if m.name.startswith("glass") else paint
    bpy.ops.mesh.primitive_plane_add(size=40, location=(0, 0, 0))
    ground = bpy.context.active_object
    gm = bpy.data.materials.new("ground")
    gm.use_nodes = True
    gm.node_tree.nodes.get("Principled BSDF").inputs["Base Color"].default_value = (0.2, 0.26, 0.12, 1)
    ground.data.materials.append(gm)
    km = bpy.data.materials.new("kerbal")
    km.use_nodes = True
    km.node_tree.nodes.get("Principled BSDF").inputs["Base Color"].default_value = (0.55, 0.62, 0.25, 1)
    o = cylinder("kerbal body", (1.1, -0.6, 0.0), (1.1, -0.6, 0.75), 0.22, "paint", 16, into=None)
    o.data.materials[0] = km
    o = sphere("kerbal head", (1.1, -0.6, 0.97), 0.25, "paint", into=None, segs=20)
    o.data.materials[0] = km
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 4.0
    sun.data.angle = math.radians(2)
    sun.rotation_euler = (math.radians(50), 0, math.radians(35))
    scene.collection.objects.link(sun)
    world = bpy.data.worlds.new("sky")
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    bg.inputs[0].default_value = (0.55, 0.65, 0.8, 1)
    bg.inputs[1].default_value = 0.8
    scene.world = world
    cam = bpy.data.objects.new("camera", bpy.data.cameras.new("camera"))
    cam.data.lens = 45
    scene.collection.objects.link(cam)
    scene.camera = cam
    scene.render.resolution_x, scene.render.resolution_y = (640, 360) if QUICK else (1280, 720)
    scene.cycles.samples = 24 if QUICK else 96
    views = {"front_left": (-2.6, 3.4, 2.0), "back_right": (2.8, -3.2, 2.4), "side": (4.2, 0.3, 1.6), "head": (-0.9, 1.6, 2.9)}
    for name, at in views.items():
        cam.location = at
        look = Vector((0, 0.1, 1.45 if name != "head" else 2.0)) - Vector(at)
        cam.rotation_euler = look.to_track_quat("-Z", "Y").to_euler()
        scene.render.filepath = os.path.join(folder, f"floodlight_{name}.png")
        bpy.ops.render.render(write_still=True)
        print("rendered", scene.render.filepath)
    # lit, at dusk
    for i in lit_slots:
        head.data.materials[i] = lit
    sun.data.energy = 0.25
    bg.inputs[1].default_value = 0.08
    for name in ("front_left", "head"):
        cam.location = views[name]
        look = Vector((0, 0.1, 1.45 if name != "head" else 2.0)) - Vector(views[name])
        cam.rotation_euler = look.to_track_quat("-Z", "Y").to_euler()
        scene.render.filepath = os.path.join(folder, f"floodlight_{name}_lit.png")
        bpy.ops.render.render(write_still=True)
        print("rendered", scene.render.filepath)


# ---------------------------------------------------------------- the whole thing
def main():
    os.makedirs(OUT, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = SAMPLES
    make_materials()
    build_stand()
    build_head()
    stand = join("stand", PARTS["stand"])
    head = join("head", PARTS["head"])
    unwrap([stand, head], 0.004)
    # (baked with the head turned up as it stands round a pad, so that the yoke shades its trunnions and the head the yoke)
    head.location = (0, 0, PIVOT_Z)
    head.rotation_euler = (math.radians(BAKE_TILT), 0, 0)
    bpy.ops.mesh.primitive_plane_add(size=6, location=(0, 0, 0))
    ground = bpy.context.active_object
    ground.data.materials.append(MATERIALS["concrete"])
    world = bpy.data.worlds.new("bake")
    scene.world = world
    world.light_settings.distance = 0.35
    both = [stand, head]
    col, ao, nor, gl, sh = image("colour", ATLAS), image("ao", ATLAS, False), image("normal", ATLAS, False), image("gloss", ATLAS, False), image("shiny", ATLAS, False)
    bake(both, "DIFFUSE", col, pass_filter={"COLOR"})
    bake(both, "AO", ao, samples=SAMPLES)
    bake(both, "NORMAL", nor, normal_space="TANGENT")
    swap(both, lambda m: m["gloss"])
    bake(both, "DIFFUSE", gl, pass_filter={"COLOR"})
    swap(both, lambda m: 1.0 if m.name.split(".")[0] in SHINY else 0.0)
    bake(both, "DIFFUSE", sh, pass_filter={"COLOR"})
    bpy.data.objects.remove(ground, do_unlink=True)
    compose(col, ao, gl, sh, "floodlight_colour.png")
    save(pixels(nor), "floodlight_normal.png", colour=False)
    head.location = (0, 0, 0)
    head.rotation_euler = (0, 0, 0)
    write_mesh({"stand": (stand, Vector((0, 0, 0))), "head": (head, Vector((0, 0, PIVOT_Z)))}, {"glass": Vector((0, GLASS_Y, 0))},
               os.path.join(OUT, "floodlight.mesh"))
    print(f"triangles: stand {len(stand.data.loop_triangles)}, head {len(head.data.loop_triangles)}; texture {ATLAS}")
    if RENDER:
        head.location = (0, 0, PIVOT_Z)
        head.rotation_euler = (math.radians(BAKE_TILT), 0, 0)
        render(RENDER, stand, head)


main()
