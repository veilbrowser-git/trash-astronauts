"""
Builds the player's space suit in Blender and writes it out for the Unity project.

Run headless from the project root:
    /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup -P ModelSource/Player/build_player.py
Add  -- --render <folder>  to also render preview stills of the assembled suit (Cycles, CPU).

It writes, next to this file (the same format the spiders use, see ModelSource/Spiders/build_spiders.py):
    astronaut.blend        the suit, assembled and posed, to open and edit in Blender
    astronaut.mesh.json    part list (name, parent, pivot, material, ranges into the .bin) and the rig numbers
    astronaut.mesh.bin     vertex data in Unity's coordinate system (float32 pos, normal, uv; uint32 indices)
    astronaut_atlas.png    the one 1024 x 1024 colour texture every part uses

The Unity generator (Builder.Avatar.cs) turns those into meshes and a PlayerAvatar, which poses the parts
from three tracked points (head and two hands).

Parts, each with its own origin:
    helmet          shell, collar ring, lamp, comms pod            origin: centre of the head
    helmet_visor    the dark visor (its own glossy material)
    helmet_accent   crew-colour stripe over the crown
    torso           chest, shoulders, control box, backpack...     origin: base of the neck
    torso_collar    the neck ring and bellows the helmet turns in (hidden with the helmet in first person)
    torso_accent    crew-colour chest stripe and shoulder patches
    upperarm        one arm segment, UNIT length along Unity +Z    origin: the shoulder joint
    upperarm_accent crew-colour band round the upper arm
    forearm         one arm segment, UNIT length along Unity +Z    origin: the elbow joint
    legs            both legs, knee pads and boots                 origin: the hips
    legs_accent     crew-colour band round each thigh
The arm segments are modelled at their real length and squashed to length 1 on export, so the game
can place a segment at a joint, aim it and scale it by the real length (see "length" in the json).

How it is modelled: every soft part is a low control cage lofted in bmesh and rounded by a Subdivision
Surface modifier; hard parts (backpack, control box, lamp, knee pads) are boxes with a Bevel modifier;
rings are lathed. The fabric folds are sculpted by pushing the subdivided surface along its normals with
a fold height field, and the same field shades the texture, so creases in the mesh and in the paint agree.
Ambient occlusion is ray cast per vertex and baked into the atlas with the weave, seams, scuffs and dirt.

Blender axes: X right, -Y forward, Z up (helper P(x, forward, up)). Export maps (x, y, z) -> (x, z, -y).
"""
import bpy, bmesh, json, math, os, struct, sys, time
import numpy as np
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

OUT = os.path.dirname(os.path.abspath(__file__))
MODEL_ID = "astronaut"
SIZE = 1024
SMOOTHNESS = 0.3

# ----------------------------------------------------------------------------- rig numbers (metres)
# Written to the json in Unity axes (x right, y up, z forward) so the game and the model cannot disagree.
EYES_TO_HEAD_CENTRE = (0.0, 0.0, -0.085)   # the tracked head point is between the eyes
NECK_DROP = 0.17                           # head centre down to the base of the neck (torso origin)
SHOULDER_X, SHOULDER_UP = 0.245, -0.09     # right shoulder joint from the torso origin
HIP_DROP = 0.52                            # torso origin down to the hips (legs origin)
UPPER_ARM, FOREARM = 0.31, 0.27

X, Y, Z = Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))


def P(x, forward, up):
    """A point given the way the game thinks: right, forward, up."""
    return Vector((x, -forward, up))


# ----------------------------------------------------------------------------- atlas layout (u0, u1, v0, v1)
REGION = {
    "torso":     (0.0, 0.5, 0.5, 1.0),
    "leg":       (0.5, 1.0, 0.5, 1.0),
    "helmet":    (0.0, 0.5, 0.25, 0.5),
    "upperarm":  (0.5, 0.75, 0.25, 0.5),
    "forearm":   (0.75, 1.0, 0.25, 0.5),
    "pack_back": (0.0, 0.1875, 0.0, 0.25),
    "hard":      (0.1875, 0.3125, 0.0, 0.25),
    "box_front": (0.3125, 0.5, 0.125, 0.25),
    "steel":     (0.3125, 0.40625, 0.0, 0.125),
    "rubber":    (0.40625, 0.5, 0.0, 0.125),
    "boot":      (0.5, 0.75, 0.0, 0.25),
    "shoulder":  (0.75, 0.875, 0.125, 0.25),
    "pad":       (0.875, 1.0, 0.125, 0.25),
    "hose":      (0.75, 0.8125, 0.0, 0.125),
    "dark":      (0.8125, 0.875, 0.0, 0.125),
    "lens":      (0.875, 0.9375, 0.0, 0.125),
    "tank":      (0.9375, 1.0, 0.0, 0.125),
}
PAD = 3.0 / SIZE
ACCENT_RECTS = {}     # region -> [(u0, u1, v0, v1)] in the region's own 0..1 space: painted bright, tinted in game


def to_atlas(region, u, v):
    u0, u1, v0, v1 = REGION[region]
    return (u0 + PAD + u * (u1 - u0 - 2 * PAD), v0 + PAD + v * (v1 - v0 - 2 * PAD))


def rect_px(region):
    u0, u1, v0, v1 = REGION[region]
    return int(round(v0 * SIZE)), int(round(v1 * SIZE)), int(round(u0 * SIZE)), int(round(u1 * SIZE))


# ----------------------------------------------------------------------------- noise (numpy, tileable in u)
def _hash(ix, iy, seed):
    h = (ix.astype(np.int64) + 100003) * 374761393 + (iy.astype(np.int64) + 100019) * 668265263 + seed * 1442695041
    h &= 0xFFFFFFFF
    h = ((h ^ (h >> 13)) * 1274126177) & 0xFFFFFFFF
    h = ((h ^ (h >> 16)) * 2246822519) & 0xFFFFFFFF
    return ((h ^ (h >> 15)) & 0xFFFFFF) / float(0xFFFFFF)


def vnoise(x, y, seed=0, px=0):
    x0, y0 = np.floor(x), np.floor(y)
    fx, fy = x - x0, y - y0
    ix, iy = x0.astype(np.int64), y0.astype(np.int64)
    sx, sy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)

    def at(dx, dy):
        jx = ix + dx
        if px:
            jx = np.mod(jx, px)
        return _hash(jx, iy + dy, seed)
    a = at(0, 0) + (at(1, 0) - at(0, 0)) * sx
    b = at(0, 1) + (at(1, 1) - at(0, 1)) * sx
    return a + (b - a) * sy


def fbm(x, y, seed=0, px=0, octaves=4):
    total, amp, norm = 0.0, 1.0, 0.0
    for o in range(octaves):
        total = total + amp * vnoise(x * (1 << o), y * (1 << o), seed + o * 17, px * (1 << o) if px else 0)
        norm += amp
        amp *= 0.5
    return total / norm


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def band(x, a, b, soft):
    """1 between a and b, fading to 0 over `soft` outside."""
    return smoothstep(a - soft, a, x) * (1.0 - smoothstep(b, b + soft, x))


# ----------------------------------------------------------------------------- fold height fields
# One per fabric region, in the region's own space (row = v along the limb, column = u round it).
# "lo" is sculpted into the mesh; "lo" and "hi" together shade the texture.
FOLD_SPEC = {
    # region: (seed, lo frequencies along v, hi frequencies, geometry amplitude in metres, joint mask)
    "torso":    (3, (5.0, 9.0), (17.0, 29.0), 0.0075,
                 lambda u, v: np.maximum(band(v, 0.5, 0.74, 0.08), 0.75 * smoothstep(0.86, 1.0, v))),
    "leg":      (5, (5.0, 8.0), (19.0, 33.0), 0.0075,
                 lambda u, v: np.maximum(np.maximum(band(v, 0.4, 0.6, 0.08), 0.8 * (1 - smoothstep(0.08, 0.2, v))), 0.8 * smoothstep(0.84, 0.94, v))),
    "upperarm": (7, (3.0, 5.0), (11.0, 17.0), 0.0045,
                 lambda u, v: np.maximum(0.8 * (1 - smoothstep(0.12, 0.3, v)), smoothstep(0.62, 0.8, v))),
    "forearm":  (9, (3.0, 5.0), (11.0, 19.0), 0.0035,
                 lambda u, v: np.maximum(1 - smoothstep(0.3, 0.42, v), 0.6 * smoothstep(0.76, 0.86, v))),
    "shoulder": (11, (2.0, 3.0), (7.0, 11.0), 0.0025, lambda u, v: 0.5 + 0.0 * u),
}
FOLDS = {}


def make_folds():
    for region, (seed, f_lo, f_hi, amp, mask) in FOLD_SPEC.items():
        y0, y1, x0, x1 = rect_px(region)
        h, w = y1 - y0, x1 - x0
        V, U = np.mgrid[0:h, 0:w]
        u, v = (U + 0.5) / w, (V + 0.5) / h
        warp1 = fbm(u * 3, v * 4, seed, px=3) - 0.5
        warp2 = fbm(u * 5, v * 7, seed + 7, px=5) - 0.5

        def crease(freq, m, warp, amount, sharp):
            # long fold lines running round the limb, pinched into creases, wandering with the warp
            return np.abs(np.sin(np.pi * (freq * v + m * u + amount * warp))) ** sharp
        lo = 0.6 * crease(f_lo[0], 1, warp1, 1.7, 0.8) + 0.4 * crease(f_lo[1], -1, warp2, 1.3, 0.8)
        hi = 0.5 * crease(f_hi[0], 2, warp2, 2.6, 0.7) + 0.5 * crease(f_hi[1], -3, warp1, 3.2, 0.7)
        patch = np.clip(fbm(u * 4, v * 5, seed + 3, px=4) * 1.6 - 0.25, 0, 1)
        strength = (0.22 + 0.78 * np.clip(mask(u, v), 0, 1)) * (0.55 + 0.45 * patch)
        FOLDS[region] = dict(lo=(lo - lo.mean()) * strength, hi=(hi - hi.mean()) * strength, amp=amp, strength=strength)


def sample_fold(region, u, v):
    """Bilinear sample of the sculpted fold height (wraps round the limb)."""
    lo = FOLDS[region]["lo"]
    h, w = lo.shape
    x = u * w - 0.5
    y = np.clip(v * h - 0.5, 0, h - 1.001)
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = x - x0, y - y0
    xa, xb = np.mod(x0, w), np.mod(x0 + 1, w)
    return (lo[y0, xa] * (1 - fx) + lo[y0, xb] * fx) * (1 - fy) + (lo[y0 + 1, xa] * (1 - fx) + lo[y0 + 1, xb] * fx) * fy


# ----------------------------------------------------------------------------- geometry containers
def weld_groups(pos):
    keys = np.round(pos / 2e-5).astype(np.int64)
    _, inverse = np.unique(keys, axis=0, return_inverse=True)
    return inverse.reshape(-1)


def corner_normals(pos, tris, angle):
    """Smooth normals per triangle corner. Faces meeting at more than `angle` degrees keep a hard edge."""
    groups = weld_groups(pos)
    a, b, c = pos[tris[:, 0]], pos[tris[:, 1]], pos[tris[:, 2]]
    fn = np.cross(b - a, c - a)                                  # length = twice the area: area weighting
    gt = groups[tris]
    if angle >= 179.0:
        acc = np.zeros((groups.max() + 1, 3))
        for k in range(3):
            np.add.at(acc, gt[:, k], fn)
        acc /= np.maximum(np.linalg.norm(acc, axis=1, keepdims=True), 1e-12)
        return acc[gt]
    unit = fn / np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-12)
    limit = math.cos(math.radians(angle))
    flat = gt.ravel()
    order = np.argsort(flat, kind="stable")
    bounds = np.flatnonzero(np.diff(flat[order])) + 1
    out = np.zeros((len(tris) * 3, 3))
    for idx in np.split(order, bounds):
        tids = idx // 3
        near = (unit[tids] @ unit[tids].T) >= limit
        res = near.astype(float) @ fn[tids]
        res /= np.maximum(np.linalg.norm(res, axis=1, keepdims=True), 1e-12)
        out[idx] = res
    return out.reshape(-1, 3, 3)


class Chunk:
    """A triangle soup with per-corner UVs: one modelled object, or a piece split off one."""

    def __init__(self, name, pos, tris, uv, region=None, material="suit", angle=180.0, bake=True):
        self.name = name
        self.pos = np.asarray(pos, dtype=float).reshape(-1, 3)
        self.tris = np.asarray(tris, dtype=np.int64).reshape(-1, 3)
        self.uv = np.asarray(uv, dtype=float).reshape(-1, 3, 2)
        self.region, self.material, self.angle = region, material, angle
        self.bake = np.full(len(self.tris), bool(bake)) if np.isscalar(bake) else np.asarray(bake, dtype=bool)
        self.cn = None        # corner normals (m, 3, 3)
        self.ao = None        # per vertex

    def local_uv(self):
        u0, u1, v0, v1 = REGION[self.region]
        out = np.empty_like(self.uv)
        out[..., 0] = (self.uv[..., 0] - u0 - PAD) / (u1 - u0 - 2 * PAD)
        out[..., 1] = (self.uv[..., 1] - v0 - PAD) / (v1 - v0 - 2 * PAD)
        return out

    def normals(self):
        self.cn = corner_normals(self.pos, self.tris, self.angle)
        return self

    def vertex_normals(self):
        return corner_normals(self.pos, self.tris, 180.0), weld_groups(self.pos)

    def split(self, mask, name, material):
        """Moves the masked triangles into a new chunk (sharing vertices, normals and occlusion)."""
        other = Chunk(name, self.pos, self.tris[mask], self.uv[mask], self.region, material, self.angle, self.bake[mask])
        other.cn = self.cn[mask] if self.cn is not None else None
        other.ao = self.ao
        keep = ~mask
        self.tris, self.uv, self.bake = self.tris[keep], self.uv[keep], self.bake[keep]
        if self.cn is not None:
            self.cn = self.cn[keep]
        return other

    def mirrored(self, name=None):
        other = Chunk(name or self.name + "_L", self.pos * np.array([-1.0, 1.0, 1.0]), self.tris[:, [0, 2, 1]], self.uv[:, [0, 2, 1]],
                      self.region, self.material, self.angle, self.bake)
        return other

    def moved(self, offset):
        self.pos = self.pos + np.array(offset, dtype=float)
        return self

    def sculpt(self):
        """Pushes the surface along its normals by the region's fold field (the sculpted folds)."""
        spec = FOLDS.get(self.region)
        if spec is None:
            return self
        local = self.local_uv()
        vert_uv = np.zeros((len(self.pos), 2))
        vert_uv[self.tris.ravel()] = local.reshape(-1, 2)
        normals, groups = self.vertex_normals()
        vn = np.zeros((len(self.pos), 3))
        vn[self.tris.ravel()] = normals.reshape(-1, 3)
        height = sample_fold(self.region, np.mod(vert_uv[:, 0], 1.0), np.clip(vert_uv[:, 1], 0, 1))
        self.pos = self.pos + vn * (height * spec["amp"] / 0.5)[:, None]
        return self


CAGES = None      # collection holding every modelled object with its modifiers still live


def obj_from_bm(bm, name):
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    for poly in mesh.polygons:
        poly.use_smooth = True
    obj = bpy.data.objects.new(name, mesh)
    CAGES.objects.link(obj)
    return obj


def chunk_from_object(obj, region=None, material="suit", angle=180.0, bake=True):
    """Evaluates the object's modifiers and returns the result as triangles in world space."""
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    mesh.calc_loop_triangles()
    n = len(mesh.vertices)
    pos = np.empty(n * 3)
    mesh.vertices.foreach_get("co", pos)
    pos = pos.reshape(-1, 3)
    m = np.array(obj.matrix_world)
    pos = pos @ m[:3, :3].T + m[:3, 3]
    count = len(mesh.loop_triangles)
    tris = np.empty(count * 3, dtype=np.int32)
    loops = np.empty(count * 3, dtype=np.int32)
    mesh.loop_triangles.foreach_get("vertices", tris)
    mesh.loop_triangles.foreach_get("loops", loops)
    if mesh.uv_layers:
        data = np.empty(len(mesh.loops) * 2)
        mesh.uv_layers[0].data.foreach_get("uv", data)
        uv = data.reshape(-1, 2)[loops].reshape(-1, 3, 2)
    else:
        uv = np.zeros((count, 3, 2))
    evaluated.to_mesh_clear()
    return Chunk(obj.name, pos, tris.reshape(-1, 3), uv, region, material, angle, bake)


def spow(x, e):
    return math.copysign(abs(x) ** e, x)


def loft(name, rings, n, region, start=0.0, cap0=None, cap1=None, levels=1, material="suit", angle=180.0, bake=True, vs=None, info=None):
    """
    A surface through a row of rings, rounded by a Subdivision Surface modifier.
    rings: (centre, x axis * radius, y axis * radius, superellipse exponent). cap0 / cap1: None leaves the end open,
    a number closes it with a point pushed out that far. UV: u round the ring, v along it (by distance unless vs is given).
    """
    comp = 4.0 / (3.0 + math.cos(math.tau / n)) if levels > 0 else 1.0   # subdivision pulls a ring inward: push the cage out
    if vs is None:
        dist = [0.0]
        for i in range(1, len(rings)):
            dist.append(dist[-1] + (rings[i][0] - rings[i - 1][0]).length)
        vs = [d / dist[-1] for d in dist]
    if info is not None:
        info["vs"] = vs
    bm = bmesh.new()
    layer = bm.loops.layers.uv.new("UVMap")
    grid = []
    for centre, ax, ay, p in rings:
        row = []
        for j in range(n):
            a = start + math.tau * j / n
            e = 2.0 / p
            row.append(bm.verts.new(centre + ax * (comp * spow(math.cos(a), e)) + ay * (comp * spow(math.sin(a), e))))
        grid.append(row)
    flip = rings[0][1].cross(rings[0][2]).dot(rings[1][0] - rings[0][0]) < 0

    def face(verts, uvs):
        if flip:
            verts, uvs = verts[::-1], uvs[::-1]
        f = bm.faces.new(verts)
        for loop, uv in zip(f.loops, uvs):
            loop[layer].uv = to_atlas(region, uv[0], uv[1])
    for i in range(len(rings) - 1):
        for j in range(n):
            j1 = (j + 1) % n
            face([grid[i][j], grid[i][j1], grid[i + 1][j1], grid[i + 1][j]],
                 [(j / n, vs[i]), ((j + 1) / n, vs[i]), ((j + 1) / n, vs[i + 1]), (j / n, vs[i + 1])])
    along0 = (rings[1][0] - rings[0][0]).normalized()
    along1 = (rings[-1][0] - rings[-2][0]).normalized()
    if cap0 is not None:
        pole = bm.verts.new(rings[0][0] - along0 * cap0)
        for j in range(n):
            j1 = (j + 1) % n
            face([grid[0][j1], grid[0][j], pole], [((j + 1) / n, vs[0]), (j / n, vs[0]), ((j + 0.5) / n, vs[0])])
    if cap1 is not None:
        pole = bm.verts.new(rings[-1][0] + along1 * cap1)
        for j in range(n):
            j1 = (j + 1) % n
            face([grid[-1][j], grid[-1][j1], pole], [(j / n, vs[-1]), ((j + 1) / n, vs[-1]), ((j + 0.5) / n, vs[-1])])
    obj = obj_from_bm(bm, name)
    if levels > 0:
        mod = obj.modifiers.new("Subdivision", "SUBSURF")
        mod.levels = levels
        mod.render_levels = levels
        mod.uv_smooth = "NONE"
    return chunk_from_object(obj, region, material, angle, bake)


def path_rings(points, ref=X):
    """Rings along a bending path: (centre, radius x, radius y, exponent) -> loft rings, frames following the path."""
    out = []
    for i, (c, rx, ry, p) in enumerate(points):
        a = points[max(i - 1, 0)][0]
        b = points[min(i + 1, len(points) - 1)][0]
        t = (b - a).normalized()
        ax = (ref - t * ref.dot(t)).normalized()
        ay = t.cross(ax)
        out.append((c, ax * rx, ay * ry, p))
    return out


def lathe(name, profile, seg, region, centre=(0, 0, 0), axis=Z, ref=None, scale=(1.0, 1.0), p=2.0, material="suit", angle=42.0, bake=False):
    """
    Spins a profile of (radius, height) points round an axis. List the profile anticlockwise (solid on its left)
    so the faces point outward. UV: u round, v along the profile.
    """
    centre, az = Vector(centre), axis.normalized()
    if ref is None:
        ref = X if abs(az.dot(X)) < 0.9 else Z
    ax = (ref - az * ref.dot(az)).normalized()
    ay = az.cross(ax)
    dist = [0.0]
    for k in range(1, len(profile)):
        dist.append(dist[-1] + math.hypot(profile[k][0] - profile[k - 1][0], profile[k][1] - profile[k - 1][1]))
    bm = bmesh.new()
    layer = bm.loops.layers.uv.new("UVMap")
    e = 2.0 / p
    grid = []
    for r, h in profile:
        if r <= 1e-9:
            grid.append([bm.verts.new(centre + az * h)] * seg)
        else:
            grid.append([bm.verts.new(centre + ax * (r * scale[0] * spow(math.cos(math.tau * j / seg), e))
                                      + ay * (r * scale[1] * spow(math.sin(math.tau * j / seg), e)) + az * h) for j in range(seg)])
    for k in range(len(profile) - 1):
        for j in range(seg):
            j1 = (j + 1) % seg
            quad = [(grid[k][j], (j / seg, dist[k])), (grid[k][j1], ((j + 1) / seg, dist[k])),
                    (grid[k + 1][j1], ((j + 1) / seg, dist[k + 1])), (grid[k + 1][j], (j / seg, dist[k + 1]))]
            seen, corners = set(), []
            for vert, uv in quad:
                if vert.index not in seen and id(vert) not in seen:
                    seen.add(id(vert))
                    corners.append((vert, uv))
            if len(corners) < 3:
                continue
            f = bm.faces.new([c[0] for c in corners])
            for loop, (_, uv) in zip(f.loops, corners):
                loop[layer].uv = to_atlas(region, uv[0], uv[1] / dist[-1])
    obj = obj_from_bm(bm, name)
    return chunk_from_object(obj, region, material, angle, bake)


def rbox(name, centre, dims, bevel, faces, rot=None, segments=2, material="suit", angle=38.0):
    """
    A box rounded by a Bevel modifier. `faces` maps a side ("+x", "-y", ... or "*") to the atlas region it is
    projected onto; sides that get their own region are baked with occlusion.
    """
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((v.co.x * dims[0], v.co.y * dims[1], v.co.z * dims[2]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    obj = obj_from_bm(bm, name)
    rot = rot if rot is not None else Matrix.Identity(4)
    obj.matrix_world = Matrix.Translation(Vector(centre)) @ rot
    if bevel > 0:
        mod = obj.modifiers.new("Bevel", "BEVEL")
        mod.width = bevel
        mod.segments = segments
        mod.limit_method = "ANGLE"
    chunk = chunk_from_object(obj, None, material, angle, True)
    r = np.array(rot.to_3x3())
    local = (chunk.pos - np.array(centre)) @ r / np.array(dims)       # -0.5 .. 0.5 on each axis
    a, b, c = chunk.pos[chunk.tris[:, 0]], chunk.pos[chunk.tris[:, 1]], chunk.pos[chunk.tris[:, 2]]
    fn = np.cross(b - a, c - a) @ r
    major = np.argmax(np.abs(fn), axis=1)
    sign = np.sign(fn[np.arange(len(fn)), major])
    names = ["x", "y", "z"]
    uv = np.zeros((len(chunk.tris), 3, 2))
    bake = np.zeros(len(chunk.tris), dtype=bool)
    for t in range(len(chunk.tris)):
        key = ("+" if sign[t] >= 0 else "-") + names[major[t]]
        region = faces.get(key, faces["*"])
        bake[t] = key in faces
        iu, iv = {0: (1, 2), 1: (0, 2), 2: (0, 1)}[int(major[t])]
        for k in range(3):
            q = local[chunk.tris[t, k]]
            uv[t, k] = to_atlas(region, min(max(q[iu] + 0.5, 0.0), 1.0), min(max(q[iv] + 0.5, 0.0), 1.0))
    chunk.uv, chunk.bake = uv, bake
    chunk.region = None
    return chunk


def hose(name, pts, radius, sides, region, segs=10):
    """A corrugated hose swept along a smooth curve through the points."""
    pts = [Vector(p) for p in pts]
    ext = [pts[0] * 2 - pts[1]] + pts + [pts[-1] * 2 - pts[-2]]
    path = []
    spans = len(pts) - 1
    for i in range(segs + 1):
        f = i / segs * spans
        k = min(int(f), spans - 1)
        t = f - k
        p0, p1, p2, p3 = ext[k], ext[k + 1], ext[k + 2], ext[k + 3]
        path.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t))
    rings = []
    normal = None
    for i, c in enumerate(path):
        t = (path[min(i + 1, segs)] - path[max(i - 1, 0)]).normalized()
        if normal is None:
            ref = Z if abs(t.dot(Z)) < 0.9 else X
            normal = (ref - t * ref.dot(t)).normalized()
        else:
            normal = (normal - t * normal.dot(t)).normalized()
        rings.append((c, normal * radius, t.cross(normal) * radius, 2.0))
    return loft(name, rings, sides, region, levels=0, bake=False)


# ----------------------------------------------------------------------------- the parts
def build_helmet():
    """Origin: the centre of the head. A rounded shell with its poles at the ears, so the crown stripe and the
    wrap-round visor are clean bands of the same surface."""
    chunks = []
    rx, rf, ru = 0.158, 0.172, 0.168
    ks = [-0.94, -0.66, -0.40, -0.14, 0.14, 0.40, 0.66, 0.94]
    rings = []
    for k in ks:
        s = math.sqrt(1 - k * k)
        rings.append((Vector((k * rx, 0, 0)), Z * (ru * s), -Y * (rf * s), 2.0))
    shell = loft("helmet_shell", rings, 16, "helmet", start=math.pi, cap0=rx * 0.06, cap1=rx * 0.06, vs=[(k + 1) / 2 for k in ks])
    ext = np.abs(shell.pos).max(axis=0)
    shell.pos = shell.pos * (np.array([rx, rf, ru]) / ext)        # subdivision shrinks a sphere: fit it back to size
    centre = shell.local_uv().mean(axis=1)
    u, v = centre[:, 0], centre[:, 1]
    visor = (v > 0.17) & (v < 0.83) & (u > 0.625) & (u < 0.84375)
    stripe = (v > 0.43) & (v < 0.57) & (u > 0.09375) & (u < 0.59375)
    ACCENT_RECTS["helmet"] = [(0.09375, 0.59375, 0.43, 0.57)]
    # the visor sits a few millimetres back from the shell, in a soft gasket
    inner = np.zeros(len(shell.pos), dtype=bool)
    inner[shell.tris[visor].ravel()] = True
    inner[shell.tris[~visor].ravel()] = False
    normals, _ = shell.vertex_normals()
    vn = np.zeros((len(shell.pos), 3))
    vn[shell.tris.ravel()] = normals.reshape(-1, 3)
    shell.pos[inner] -= vn[inner] * 0.005
    hidden = (shell.pos[shell.tris][:, :, 2] < -0.132).all(axis=1)   # inside the collar
    shell.tris, shell.uv, shell.bake = shell.tris[~hidden], shell.uv[~hidden], shell.bake[~hidden]
    visor, stripe = visor[~hidden], stripe[~hidden]
    shell.normals()
    chunks.append(shell)
    split = [(visor, "helmet_visor", "visor"), (stripe, "helmet_stripe", "accent")]

    # collar: the locking ring the helmet sits in
    chunks.append(lathe("helmet_collar", [(0.0, -0.160), (0.114, -0.160), (0.121, -0.152), (0.121, -0.144), (0.135, -0.139),
                                          (0.137, -0.118), (0.132, -0.112), (0.116, -0.106)], 20, "steel"))
    for i in range(6):
        a = math.tau * (i + 0.5) / 6
        chunks.append(rbox("helmet_lug_%d" % i, Vector((math.cos(a) * 0.138, math.sin(a) * 0.138, -0.125)), (0.012, 0.026, 0.018), 0.0,
                           {"*": "dark"}, rot=Matrix.Rotation(a, 4, "Z")))
    # visor pivots over the ears
    for side in (-1, 1):
        chunks.append(lathe("helmet_hub_%d" % side, [(0.052, -0.014), (0.052, 0.004), (0.045, 0.011), (0.02, 0.013), (0.0, 0.013)], 12, "steel",
                            centre=(side * 0.150, 0.0, 0.0), axis=X * side))
    # lamp on the right, comms pod with a stub antenna on the left
    chunks.append(rbox("helmet_lamp", P(0.146, 0.035, 0.075), (0.046, 0.088, 0.05), 0.011, {"*": "hard"}))
    chunks.append(lathe("helmet_lamp_lens", [(0.02, -0.006), (0.02, 0.004), (0.016, 0.007), (0.0, 0.008)], 10, "lens", centre=P(0.146, 0.079, 0.075), axis=-Y))
    chunks.append(rbox("helmet_comms", P(-0.150, 0.01, 0.07), (0.036, 0.062, 0.04), 0.009, {"*": "hard"}))
    chunks.append(loft("helmet_antenna", [(P(-0.158, -0.012, 0.085), X * 0.0045, Y * 0.0045, 2.0), (P(-0.158, -0.012, 0.2), X * 0.003, Y * 0.003, 2.0)],
                       5, "dark", cap1=0.004, levels=0, bake=False))
    return chunks, split


TORSO_RINGS = [  # up, half width, half depth, forward shift, squareness
    (0.012, 0.146, 0.136, 0.000, 2.0),
    (-0.022, 0.176, 0.146, 0.002, 2.2),
    (-0.065, 0.216, 0.150, 0.006, 2.4),     # 2: chest stripe from here...
    (-0.125, 0.198, 0.154, 0.010, 2.5),     # 3: ...to here
    (-0.200, 0.182, 0.152, 0.012, 2.5),
    (-0.275, 0.173, 0.145, 0.010, 2.4),
    (-0.330, 0.165, 0.138, 0.006, 2.3),
    (-0.355, 0.175, 0.147, 0.006, 2.3),     # waist bellows: crest
    (-0.380, 0.162, 0.135, 0.004, 2.3),     #                valley
    (-0.405, 0.175, 0.147, 0.004, 2.3),     #                crest
    (-0.430, 0.164, 0.137, 0.004, 2.3),     #                valley
    (-0.470, 0.186, 0.150, 0.004, 2.4),
    (-0.530, 0.194, 0.154, 0.002, 2.4),
    (-0.585, 0.176, 0.145, 0.000, 2.3),
    (-0.625, 0.112, 0.102, 0.000, 2.0),
]


def build_torso():
    """Origin: the base of the neck."""
    chunks, split = [], []
    info = {}
    rings = [(P(0, cf, up), X * rx, -Y * ry, p) for up, rx, ry, cf, p in TORSO_RINGS]
    body = loft("torso_body", rings, 14, "torso", start=-math.pi / 2, cap0=0.0, cap1=0.02, info=info).sculpt().normals()
    vs = info["vs"]
    centre = body.local_uv().mean(axis=1)
    stripe = (centre[:, 1] > vs[2]) & (centre[:, 1] < vs[3]) & (centre[:, 0] > 9 / 28) & (centre[:, 0] < 19 / 28)
    ACCENT_RECTS["torso"] = [(9 / 28, 19 / 28, vs[2], vs[3])]
    chunks.append(body)
    split.append((body, stripe, "torso_stripe", "accent"))

    # rounded shoulders, each with a crew-colour patch on top
    info = {}
    cap = [(P(0.165 + dx, 0.0, SHOULDER_UP), Z * r, -Y * r, 2.0) for dx, r in ((0.0, 0.07), (0.068, 0.089), (0.108, 0.08), (0.134, 0.055))]
    right = loft("torso_shoulder_R", cap, 8, "shoulder", start=math.pi, cap1=0.017, info=info).sculpt().normals()
    left = right.mirrored("torso_shoulder_L").normals()
    vs = info["vs"]
    ACCENT_RECTS["shoulder"] = [(0.25, 0.75, vs[1], vs[2])]
    for shoulder in (right, left):
        centre = shoulder.local_uv().mean(axis=1)
        patch = (centre[:, 1] > vs[1]) & (centre[:, 1] < vs[2]) & (centre[:, 0] > 0.25) & (centre[:, 0] < 0.75)
        chunks.append(shoulder)
        split.append((shoulder, patch, shoulder.name + "_patch", "accent"))

    # neck: a steel ring and a rubber bellows the helmet's collar turns in. Exported as its own part (torso_collar)
    # so the wearer, who has no helmet drawn, is not left staring into an empty collar; under it the torso is closed.
    chunks.append(lathe("torso_neck_ring", [(0.138, -0.014), (0.158, -0.011), (0.163, -0.003), (0.163, 0.014), (0.157, 0.021), (0.146, 0.022)], 18, "steel"))
    chunks.append(lathe("torso_neck_bellows", [(0.147, 0.018), (0.140, 0.033), (0.128, 0.027), (0.117, 0.042), (0.104, 0.046), (0.0, 0.046)], 18, "rubber"))
    # waist: the ring the two halves of the suit lock together on
    chunks.append(lathe("torso_waist_ring", [(0.985, -0.013), (1.045, -0.010), (1.05, 0.005), (1.035, 0.012), (0.985, 0.013)], 18, "steel",
                        centre=P(0, 0.007, -0.312), scale=(0.168, 0.141), p=2.35))

    # chest control box with two knobs
    chunks.append(rbox("torso_control_box", P(0, 0.180, -0.205), (0.19, 0.048, 0.125), 0.011, {"-y": "box_front", "*": "hard"}))
    for x in (-0.062, 0.062):
        chunks.append(lathe("torso_knob", [(0.012, -0.004), (0.012, 0.011), (0.009, 0.014), (0.0, 0.014)], 8, "dark", centre=P(x, 0.204, -0.243), axis=-Y))

    # life support backpack, two tanks on its back, a beacon housing on top
    chunks.append(rbox("torso_pack", P(0, -0.215, -0.250), (0.33, 0.17, 0.50), 0.032, {"+y": "pack_back", "*": "hard"}, segments=2))
    chunks.append(rbox("torso_pack_top", P(0.09, -0.235, 0.012), (0.085, 0.085, 0.036), 0.009, {"*": "hard"}))
    chunks.append(lathe("torso_beacon", [(0.016, 0.0), (0.016, 0.01), (0.011, 0.018), (0.0, 0.021)], 8, "lens", centre=P(0.09, -0.235, 0.03)))
    tank = []
    for a in (90, 50):
        tank.append((P(0.085, -0.305, -0.41 - 0.052 * math.sin(math.radians(a))), 0.052 * math.cos(math.radians(a))))
    tank.append((P(0.085, -0.305, -0.41), 0.052))
    tank.append((P(0.085, -0.305, -0.13), 0.052))
    for a in (50, 90):
        tank.append((P(0.085, -0.305, -0.13 + 0.052 * math.sin(math.radians(a))), 0.052 * math.cos(math.radians(a))))
    tank = [(c, r) for c, r in tank if r > 0.004]
    tank_rings = [(c, X * r, Y * r, 2.0) for c, r in tank]
    right = loft("torso_tank_R", tank_rings, 8, "tank", start=math.pi / 2, cap0=0.006, cap1=0.006, levels=0, bake=False)
    chunks += [right, right.mirrored("torso_tank_L")]

    # hoses: control box round the ribs to the pack, and each tank over the shoulder to the neck ring
    front = hose("torso_hose_R", [P(0.085, 0.188, -0.252), P(0.13, 0.172, -0.292), P(0.184, 0.085, -0.312), P(0.196, -0.02, -0.314),
                                  P(0.172, -0.105, -0.31), P(0.14, -0.15, -0.31)], 0.0125, 5, "hose", segs=10)
    back = hose("torso_tank_hose_R", [P(0.085, -0.31, -0.085), P(0.086, -0.29, -0.012), P(0.08, -0.215, 0.024), P(0.068, -0.148, 0.004)], 0.011, 5, "hose", segs=6)
    chunks += [front, front.mirrored(), back, back.mirrored()]
    return chunks, split


def build_arm():
    """Two segments along -Y (Unity +Z), each from its joint at the origin. The right arm; the left is mirrored in game."""
    def rings(spec):
        return path_rings([(Vector((0, -s, 0)), r, r, 2.0) for s, r in spec])
    info = {}
    spec = [(-0.035, 0.040), (-0.008, 0.059), (0.035, 0.065), (0.105, 0.064), (0.160, 0.062), (0.240, 0.056), (0.305, 0.050), (0.332, 0.033)]
    upper = loft("upperarm_sleeve", rings(spec), 8, "upperarm", start=math.pi / 2, cap0=0.012, cap1=0.01, info=info).sculpt().normals()
    vs = info["vs"]
    centre = upper.local_uv().mean(axis=1)
    arm_band = (centre[:, 1] > vs[3]) & (centre[:, 1] < vs[4])
    ACCENT_RECTS["upperarm"] = [(0.0, 1.0, vs[3], vs[4])]
    upper_chunks = [upper,
                    lathe("upperarm_shoulder_ring", [(0.060, -0.012), (0.071, -0.010), (0.073, 0.004), (0.068, 0.012), (0.060, 0.013)],
                          12, "steel", centre=(0, -0.042, 0), axis=-Y),
                    lathe("upperarm_elbow_cuff", [(0.05, -0.011), (0.06, -0.009), (0.06, 0.008), (0.05, 0.011)], 12, "steel", centre=(0, -0.262, 0), axis=-Y)]
    upper_split = [(upper, arm_band, "upperarm_band", "accent")]

    spec = [(-0.040, 0.030), (-0.022, 0.050), (0.000, 0.058), (0.022, 0.049), (0.044, 0.057), (0.066, 0.049), (0.092, 0.055),
            (0.165, 0.051), (0.240, 0.044), (0.272, 0.042)]
    fore = loft("forearm_sleeve", rings(spec), 8, "forearm", start=math.pi / 2, cap0=0.01, cap1=0.0).sculpt().normals()
    fore_chunks = [fore,
                   lathe("forearm_wrist_ring", [(0.04, -0.014), (0.05, -0.012), (0.052, 0.005), (0.047, 0.013), (0.04, 0.014)],
                         12, "steel", centre=(0, -0.248, 0), axis=-Y)]
    return upper_chunks, upper_split, fore_chunks


def build_legs():
    """Origin: the hips. Both legs, slightly bent the way a body floats, with knee pads and boots."""
    chunks, split = [], []
    hip = P(0.100, 0.0, -0.03)
    thigh_dir = P(math.sin(math.radians(7)), math.sin(math.radians(24)) * math.cos(math.radians(7)), -math.cos(math.radians(24)) * math.cos(math.radians(7))).normalized()
    knee = hip + thigh_dir * 0.43
    shank_dir = P(0.035, -math.sin(math.radians(14)), -math.cos(math.radians(14))).normalized()
    ankle = knee + shank_dir * 0.42
    back = P(0, -1, 0)
    pts = [
        (hip - thigh_dir * 0.075, 0.070, 0.075, 2.0),
        (hip - thigh_dir * 0.02, 0.096, 0.104, 2.1),
        (hip + thigh_dir * 0.07, 0.098, 0.106, 2.1),
        (hip + thigh_dir * 0.15, 0.094, 0.100, 2.0),     # 3: thigh band from here...
        (hip + thigh_dir * 0.215, 0.089, 0.094, 2.0),    # 4: ...to here
        (hip + thigh_dir * 0.31, 0.080, 0.084, 2.0),
        (hip + thigh_dir * 0.385, 0.073, 0.077, 2.0),
        (knee + P(0, 0.006, 0), 0.076, 0.081, 2.0),
        (knee + shank_dir * 0.05, 0.068, 0.071, 2.0),
        (knee + shank_dir * 0.13 + back * 0.007, 0.069, 0.077, 2.0),
        (knee + shank_dir * 0.25 + back * 0.003, 0.062, 0.066, 2.0),
        (knee + shank_dir * 0.34, 0.056, 0.058, 2.0),
        (knee + shank_dir * 0.405, 0.054, 0.056, 2.0),
    ]
    info = {}
    leg = loft("legs_leg_R", path_rings(pts), 9, "leg", start=-math.pi / 2, cap0=0.02, info=info).sculpt().normals()
    vs = info["vs"]
    ACCENT_RECTS["leg"] = [(0.0, 1.0, vs[3], vs[4])]
    left = leg.mirrored("legs_leg_L").normals()
    for one in (leg, left):
        centre = one.local_uv().mean(axis=1)
        chunks.append(one)
        split.append((one, (centre[:, 1] > vs[3]) & (centre[:, 1] < vs[4]), one.name + "_band", "accent"))

    side = []
    # knee pad, lying along the bend of the knee
    tilt = math.radians(5)
    front = P(0, math.cos(tilt), math.sin(tilt))
    side.append(rbox("legs_knee_pad_R", knee + front * 0.074 + P(0, 0, -0.004), (0.108, 0.034, 0.128), 0.013, {"-y": "pad", "*": "pad"},
                     rot=Matrix.Rotation(-tilt, 4, "X")))
    # boot: toes hanging down and forward
    drop = math.radians(28)
    foot = P(0.03, math.cos(drop), -math.sin(drop)).normalized()
    down = P(0, -math.sin(drop), -math.cos(drop)).normalized()
    sole = [(-0.078, 0.034, 0.038, 0.048), (-0.055, 0.047, 0.058, 0.030), (0.0, 0.053, 0.066, 0.022), (0.07, 0.055, 0.050, 0.038),
            (0.14, 0.056, 0.040, 0.047), (0.198, 0.047, 0.031, 0.055)]
    boot_pts = [(ankle + foot * s + down * d, w, h, 3.0) for s, w, h, d in sole]
    side.append(loft("legs_boot_R", path_rings(boot_pts), 8, "boot", start=math.pi / 2, cap0=0.012, cap1=0.014))
    side.append(lathe("legs_ankle_ring_R", [(0.054, -0.014), (0.066, -0.011), (0.066, 0.01), (0.054, 0.014)], 12, "steel",
                      centre=knee + shank_dir * 0.352, axis=shank_dir))
    for chunk in side:
        chunks += [chunk, chunk.mirrored()]
    return chunks, split


# ----------------------------------------------------------------------------- occlusion
def bake_occlusion(chunks, reach=0.13, rays=20):
    """Per-vertex ambient occlusion: how much of the sky near each vertex is blocked by the rest of the part."""
    verts, polys = [], []
    for chunk in chunks:
        base = len(verts)
        verts += [tuple(p) for p in chunk.pos]
        polys += [tuple(int(i) + base for i in t) for t in chunk.tris]
    tree = BVHTree.FromPolygons(verts, polys)
    dirs = []
    for i in range(rays):                                    # a fixed fan over the hemisphere
        z = 0.12 + 0.88 * (i + 0.5) / rays
        a = i * 2.39996
        s = math.sqrt(1 - z * z)
        dirs.append((s * math.cos(a), s * math.sin(a), z))
    weight = sum(d[2] for d in dirs)
    for chunk in chunks:
        normals, _ = chunk.vertex_normals()
        vn = np.zeros((len(chunk.pos), 3))
        vn[chunk.tris.ravel()] = normals.reshape(-1, 3)
        ao = np.ones(len(chunk.pos))
        used = np.zeros(len(chunk.pos), dtype=bool)
        used[chunk.tris.ravel()] = True
        for i in np.flatnonzero(used):
            n = Vector(vn[i])
            if n.length < 0.5:
                continue
            t = n.orthogonal().normalized()
            b = n.cross(t)
            origin = Vector(chunk.pos[i]) + n * 0.004
            blocked = 0.0
            for dx, dy, dz in dirs:
                hit = tree.ray_cast(origin, t * dx + b * dy + n * dz, reach)
                if hit[0] is not None:
                    blocked += dz * (1.0 - hit[3] / reach)
            ao[i] = 1.0 - blocked / weight
        chunk.ao = ao


def raster_occlusion(chunks):
    """Draws each chunk's per-vertex occlusion into atlas space through its UVs."""
    ao = np.full((SIZE, SIZE), np.nan)
    for chunk in chunks:
        if chunk.ao is None:
            continue
        for t in np.flatnonzero(chunk.bake):
            (x0, y0), (x1, y1), (x2, y2) = chunk.uv[t] * SIZE
            den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
            if abs(den) < 1e-9:
                continue
            ax, bx = max(int(math.floor(min(x0, x1, x2))) - 1, 0), min(int(math.ceil(max(x0, x1, x2))) + 1, SIZE)
            ay, by = max(int(math.floor(min(y0, y1, y2))) - 1, 0), min(int(math.ceil(max(y0, y1, y2))) + 1, SIZE)
            if bx <= ax or by <= ay:
                continue
            xs, ys = np.meshgrid(np.arange(ax, bx) + 0.5, np.arange(ay, by) + 0.5)
            w0 = ((y1 - y2) * (xs - x2) + (x2 - x1) * (ys - y2)) / den
            w1 = ((y2 - y0) * (xs - x2) + (x0 - x2) * (ys - y2)) / den
            w2 = 1.0 - w0 - w1
            inside = (w0 >= -0.06) & (w1 >= -0.06) & (w2 >= -0.06)
            a0, a1, a2 = chunk.ao[chunk.tris[t]]
            view = ao[ay:by, ax:bx]
            view[inside] = (np.clip(w0, 0, 1) * a0 + np.clip(w1, 0, 1) * a1 + np.clip(w2, 0, 1) * a2)[inside]
    for _ in range(6):                                       # spread into the gutters so mip-maps do not bleed
        missing = np.isnan(ao)
        if not missing.any():
            break
        total, count = np.zeros_like(ao), np.zeros_like(ao)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            shifted = np.roll(ao, (dy, dx), axis=(0, 1))
            ok = ~np.isnan(shifted)
            total[ok] += shifted[ok]
            count[ok] += 1
        fill = missing & (count > 0)
        ao[fill] = total[fill] / count[fill]
    ao = np.where(np.isnan(ao), 1.0, ao)
    for _ in range(6):                                       # soften the facets of per-vertex shading
        blur = ao.copy()
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            blur += np.roll(ao, (dy, dx), axis=(0, 1))
        ao = blur / 5.0
    return ao


# ----------------------------------------------------------------------------- texture
FABRIC = np.array([0.86, 0.86, 0.83])
GRIME = np.array([0.33, 0.29, 0.25])


def paint_atlas(ao):
    rng = np.random.default_rng(11)
    img = np.zeros((SIZE, SIZE, 3))
    img[:] = (0.5, 0.5, 0.5)
    YY, XX = np.mgrid[0:SIZE, 0:SIZE]
    grain = rng.random((SIZE, SIZE))
    # plain weave with a ripstop grid: threads two texels wide, a heavier thread every sixteen
    weave = 1.0 + 0.045 * np.sin(XX * math.pi / 1.5) * np.sin(YY * math.pi / 1.5) + 0.035 * (grain - 0.5)
    weave *= np.where((XX % 16 == 0) | (YY % 16 == 0), 0.955, 1.0)

    def grid(region):
        y0, y1, x0, x1 = rect_px(region)
        h, w = y1 - y0, x1 - x0
        V, U = np.mgrid[0:h, 0:w]
        return (slice(y0, y1), slice(x0, x1)), (U + 0.5) / w, (V + 0.5) / h, w, h

    def scuffs(col, count, seed, dark=0.7, length=(6, 34)):
        r = np.random.default_rng(seed)
        h, w = col.shape[:2]
        for _ in range(count):
            x, y = r.uniform(0, w), r.uniform(0, h)
            a = r.uniform(0, math.tau)
            n = int(r.uniform(*length))
            k = r.uniform(dark, 0.96)
            xs = np.clip((x + np.cos(a) * np.arange(n) + r.normal(0, 0.4, n)).astype(int), 0, w - 1)
            ys = np.clip((y + np.sin(a) * np.arange(n) + r.normal(0, 0.4, n)).astype(int), 0, h - 1)
            col[ys, xs] *= k

    def seam(col, u, v, w, h, axis, at, lo=0.0, hi=1.0):
        """A stitched seam: a dark groove with a row of stitches each side."""
        if axis == "v":
            d = np.abs(v - at) * h
            along = u * w
            span = (u >= lo) & (u <= hi)
        else:
            d = np.abs(u - at) * w
            d = np.minimum(d, np.abs(np.abs(u - at) - 1.0) * w)
            along = v * h
            span = (v >= lo) & (v <= hi)
        groove = (d < 1.2) & span
        stitch = (d > 2.5) & (d < 4.2) & span & ((along.astype(int) % 5) < 3)
        lip = (d >= 1.2) & (d < 2.5) & span
        col[groove] *= 0.62
        col[lip] *= 1.05
        col[stitch] *= 0.8

    def fabric(region, seed, joints=(), seams=(), dirt=0.5, wear=120, tone=1.0, dirty=None):
        sl, u, v, w, h = grid(region)
        spec = FOLDS[region]
        height = spec["lo"] + 0.55 * spec["hi"]
        slope = np.gradient(height, axis=0) * h / 60.0
        light = np.clip(1.0 + 1.5 * slope, 0.74, 1.16)                   # lit from above: fake shading for the fine wrinkles
        cavity = np.clip(-height * 2.6, 0, 1)
        mottle = fbm(u * 6, v * 6, seed, px=6)
        col = FABRIC[None, None, :] * tone * (weave[sl] * light * (1.0 - 0.3 * cavity) * (0.93 + 0.14 * mottle))[..., None]
        for v0, v1, k in joints:                                         # bellows and joints: darker, tougher cloth
            m = band(v, v0, v1, 0.012)[..., None]
            col = col * (1 - m) + col * np.array([0.5, 0.51, 0.53]) * k * m
        accent = np.zeros_like(u, dtype=bool)
        for u0, u1, v0, v1 in ACCENT_RECTS.get(region, []):
            inside = (u >= u0) & (u <= u1) & (v >= v0) & (v <= v1)
            accent |= inside
            col[inside] = (np.array([0.97, 0.97, 0.97]) * (weave[sl] * light * (1.0 - 0.25 * cavity))[..., None])[inside]
            for at in (v0, v1):
                seam(col, u, v, w, h, "v", at, u0, u1)
            if u1 - u0 < 0.99:
                for at in (u0, u1):
                    seam(col, u, v, w, h, "u", at, v0, v1)
        for axis, at, lo, hi in seams:
            seam(col, u, v, w, h, axis, at, lo, hi)
        grime = np.clip(fbm(u * 4, v * 5, seed + 5, px=4) * 1.9 - 0.62, 0, 1) * (0.35 + 0.65 * cavity)
        if dirty is not None:
            grime = np.clip(grime + dirty(u, v) * (0.35 + 0.65 * fbm(u * 9, v * 9, seed + 9, px=9)), 0, 1)
        grime = grime * dirt * np.where(accent, 0.45, 1.0)
        col = col * (1 - grime[..., None]) + GRIME[None, None, :] * grime[..., None] * (0.8 + 0.4 * mottle[..., None])
        scuffs(col, wear, seed + 21)
        img[sl] = col
        return sl, u, v, w, h

    # ---- torso: u 0 = spine, 0.25 = right side, 0.5 = sternum; v 0 = neck, 1 = crotch
    sl, u, v, w, h = fabric("torso", 31, joints=[(0.52, 0.71, 1.0)], dirt=0.55, wear=220,
                            seams=[("u", 0.25, 0.1, 1.0), ("u", 0.75, 0.1, 1.0), ("v", 0.5, 0.0, 1.0), ("v", 0.725, 0.0, 1.0),
                                   ("u", 0.5, 0.74, 1.0), ("v", 0.09, 0.0, 1.0)],
                            dirty=lambda u, v: 0.5 * smoothstep(0.8, 1.0, v))
    col = img[sl]
    for centre in (0.5 - 0.088, 0.5 + 0.088, 0.088, 1 - 0.088):          # harness webbing over the shoulders to the pack
        strap = (np.abs(u - centre) * w < 8) & (v < 0.5)
        edge = (np.abs(np.abs(u - centre) * w - 6) < 1.0) & (v < 0.5)
        col[strap] = col[strap] * np.array([0.3, 0.31, 0.33])
        col[edge & ((YY[sl] % 4) < 2)] *= 1.7
    img[sl] = col

    # ---- legs: u 0 = back, 0.25 = outside, 0.5 = front; v 0 = hip, 1 = ankle
    sl, u, v, w, h = fabric("leg", 37, joints=[(0.43, 0.6, 1.0), (0.945, 1.0, 0.8)], dirt=0.75, wear=260,
                            seams=[("u", 0.25, 0.0, 1.0), ("u", 0.75, 0.0, 1.0), ("v", 0.4, 0.0, 1.0), ("v", 0.63, 0.0, 1.0)],
                            dirty=lambda u, v: 0.55 * smoothstep(0.7, 1.0, v) + 0.35 * band(v, 0.44, 0.58, 0.05) * band(u, 0.35, 0.65, 0.08))
    col = img[sl]
    for at in (0.475, 0.555):                                            # the knee pad's two straps
        strap = np.abs(v - at) * h < 5
        col[strap] = col[strap] * np.array([0.3, 0.31, 0.33])
        col[(np.abs(np.abs(v - at) * h - 3.5) < 0.8) & ((XX[sl] % 4) < 2)] *= 1.7
    img[sl] = col
    fabric("upperarm", 41, joints=[(0.0, 0.1, 0.9), (0.9, 1.0, 1.0)], dirt=0.5, wear=90,
           seams=[("u", 0.0, 0.0, 1.0), ("v", 0.2, 0.0, 1.0), ("v", 0.72, 0.0, 1.0)],
           dirty=lambda u, v: 0.4 * smoothstep(0.7, 1.0, v))
    fabric("forearm", 43, joints=[(0.0, 0.36, 1.0)], dirt=0.6, wear=110,
           seams=[("u", 0.0, 0.36, 1.0), ("v", 0.4, 0.0, 1.0), ("v", 0.8, 0.0, 1.0)],
           dirty=lambda u, v: 0.4 * smoothstep(0.75, 1.0, v) + 0.3 * (1 - smoothstep(0.1, 0.36, v)))
    fabric("shoulder", 47, dirt=0.4, wear=40, seams=[("v", 0.1, 0.0, 1.0)])

    # ---- helmet: u 0 = under the chin, 0.25 = back, 0.5 = crown, 0.75 = face; v = ear to ear
    sl, u, v, w, h = grid("helmet")
    scratch = fbm(u * 40, v * 6, 51, px=40)
    col = np.array([0.9, 0.9, 0.88])[None, None, :] * (0.95 + 0.07 * fbm(u * 5, v * 3, 53, px=5) + 0.03 * (grain[sl] - 0.5) - 0.05 * np.clip(scratch * 3 - 1.9, 0, 1))[..., None]
    grime = np.clip(fbm(u * 6, v * 4, 55, px=6) * 2.0 - 1.05, 0, 1) * 0.5
    col = col * (1 - grime[..., None]) + GRIME * grime[..., None]
    scuffs(col, 150, 57, dark=0.78, length=(5, 26))
    vu0, vu1, vv0, vv1 = 0.625, 0.84375, 0.17, 0.83
    du = np.maximum(vu0 - u, u - vu1) * w
    dv = np.maximum(vv0 - v, v - vv1) * h
    edge = np.maximum(du, dv)                                            # distance outside the visor, in texels
    col[(edge > 0) & (edge < 5)] = np.array([0.07, 0.07, 0.08])          # gasket
    col[(edge >= 5) & (edge < 6.5)] *= 0.72
    inside = edge <= 0
    t = (u - vu0) / (vu1 - vu0)                                          # 0 = top of the visor, 1 = chin edge
    s = (v - vv0) / (vv1 - vv0)
    glass = np.array([0.012, 0.016, 0.026])[None, None, :] * (1.4 - 0.8 * t)[..., None]
    horizon = np.exp(-((t - 0.36) / 0.07) ** 2) * (0.75 + 0.25 * np.sin(s * math.pi))
    glass = glass + np.array([0.10, 0.14, 0.2])[None, None, :] * horizon[..., None]
    glass = glass + np.array([0.02, 0.03, 0.05])[None, None, :] * np.clip(1 - t / 0.36, 0, 1)[..., None]
    for cs, ct, rs, rt, k, tint in ((0.30, 0.2, 0.06, 0.05, 0.75, (1.0, 0.86, 0.62)), (0.36, 0.27, 0.025, 0.02, 0.5, (1.0, 1.0, 1.0)),
                                    (0.74, 0.52, 0.12, 0.035, 0.12, (0.6, 0.75, 1.0))):
        glint = np.exp(-((s - cs) / rs) ** 2 - ((t - ct) / rt) ** 2) * k
        glass = glass + np.array(tint)[None, None, :] * glint[..., None]
    col[inside] = np.clip(glass, 0, 1)[inside]
    for u0, u1, v0, v1 in ACCENT_RECTS.get("helmet", []):
        stripe = (u >= u0) & (u <= u1) & (v >= v0) & (v <= v1)
        col[stripe] = (np.array([0.97, 0.97, 0.97]) * (0.97 + 0.05 * (grain[sl] - 0.5))[..., None])[stripe]
        lines = (np.minimum(np.abs(v - v0), np.abs(v - v1)) * h < 1.3) & (u >= u0) & (u <= u1)
        col[lines] = np.array([0.2, 0.2, 0.21])
    for at in (0.115, 0.885):                                            # panel line round each ear piece
        col[np.abs(v - at) * h < 0.9] *= 0.62
    img[sl] = col

    # ---- hard shells: painted, panelled, chipped at the edges, streaked
    def shell(region, seed, base=(0.74, 0.75, 0.76), chips=160):
        sl, u, v, w, h = grid(region)
        streak = fbm(u * 26, v * 2.5, seed, px=0)
        col = np.array(base)[None, None, :] * (0.94 + 0.08 * fbm(u * 4, v * 6, seed + 1) + 0.04 * (grain[sl] - 0.5) - 0.1 * np.clip(streak * 2.4 - 1.2, 0, 1) * (1 - v))[..., None]
        border = np.minimum(np.minimum(u, 1 - u) * w, np.minimum(v, 1 - v) * h)
        col[(border > 9) & (border < 10.5)] *= 0.55                      # inset panel line
        col[(border > 10.5) & (border < 11.6)] *= 1.07
        chip = np.clip(fbm(u * 30, v * 60, seed + 3) * 3 - 1.95, 0, 1) * np.clip(1.4 - border / 9.0, 0, 1)
        col = col * (1 - chip[..., None]) + np.array([0.2, 0.2, 0.21]) * chip[..., None]
        for cx, cy in ((0.1, 0.06), (0.9, 0.06), (0.1, 0.94), (0.9, 0.94), (0.5, 0.06), (0.5, 0.94)):
            rivet = ((u - cx) * w) ** 2 + ((v - cy) * h) ** 2
            col[rivet < 6] *= 0.6
            col[rivet < 2] *= 1.9
        scuffs(col, chips, seed + 5, dark=0.6, length=(4, 22))
        return sl, u, v, w, h, col

    sl, u, v, w, h, col = shell("hard", 61)
    img[sl] = col

    sl, u, v, w, h, col = shell("pack_back", 67, chips=220)
    def block(u0, u1, v0, v1):
        return (u >= u0) & (u <= u1) & (v >= v0) & (v <= v1)
    grille = block(0.16, 0.84, 0.80, 0.93)
    col[grille] = np.array([0.1, 0.1, 0.11])
    col[grille & ((YY[sl] % 5) < 2)] = np.array([0.34, 0.35, 0.36])
    label = block(0.38, 0.62, 0.66, 0.75)                                # hazard label between the tanks
    col[label] = np.array([0.82, 0.68, 0.1])
    col[label & (((XX[sl] + YY[sl]) % 10) < 5)] = np.array([0.08, 0.08, 0.08])
    plate = block(0.4, 0.6, 0.2, 0.6)                                    # service hatch with status lights
    col[plate] *= 0.82
    col[plate & ~block(0.415, 0.585, 0.21, 0.59)] *= 0.55
    for k, tint in enumerate(((0.2, 1.0, 0.6), (0.2, 1.0, 0.6), (1.0, 0.75, 0.2), (1.0, 0.2, 0.15))):
        led = ((u - 0.5) * w) ** 2 + ((v - (0.52 - k * 0.075)) * h) ** 2 < 9
        col[led] = np.array(tint)
    img[sl] = col

    # ---- control box face: display, switches, buttons, vents
    sl, u, v, w, h = grid("box_front")
    col = np.zeros((h, w, 3))
    col[:] = np.array([0.2, 0.21, 0.23]) * (0.95 + 0.1 * grain[sl])[..., None]
    border = np.minimum(np.minimum(u, 1 - u) * w, np.minimum(v, 1 - v) * h)
    col[border < 7] = np.array([0.7, 0.71, 0.72])
    col[(border >= 7) & (border < 8.5)] = np.array([0.06, 0.06, 0.07])
    def block(u0, u1, v0, v1):
        return (u >= u0) & (u <= u1) & (v >= v0) & (v <= v1)
    screen = block(0.1, 0.5, 0.48, 0.86)
    col[screen] = np.array([0.02, 0.09, 0.09])
    col[screen & ((YY[sl] % 3) == 0)] = np.array([0.03, 0.16, 0.15])
    for row, length in enumerate((0.3, 0.22, 0.34, 0.16)):
        text = block(0.13, 0.13 + length, 0.78 - row * 0.08, 0.8 - row * 0.08 + 0.015)
        col[text] = np.array([0.35, 0.95, 0.85])
    bar = block(0.13, 0.4, 0.5, 0.54)
    col[bar] = np.array([0.95, 0.75, 0.25])
    for k in range(4):                                                   # toggle switches under guards
        cx = 0.6 + k * 0.095
        col[block(cx - 0.03, cx + 0.03, 0.5, 0.86)] = np.array([0.1, 0.1, 0.11])
        col[block(cx - 0.012, cx + 0.012, 0.6 + (k % 2) * 0.08, 0.76 + (k % 2) * 0.08)] = np.array([0.78, 0.79, 0.8])
        col[block(cx - 0.02, cx + 0.02, 0.52, 0.56)] = np.array([0.9, 0.9, 0.86])
    for k, tint in enumerate(((0.85, 0.12, 0.1), (0.15, 0.7, 0.3), (0.9, 0.9, 0.9))):
        d = ((u - (0.62 + k * 0.13)) * w) ** 2 + ((v - 0.3) * h) ** 2
        col[d < 70] = np.array([0.05, 0.05, 0.05])
        col[d < 42] = np.array(tint)
        col[d < 8] = np.clip(np.array(tint) * 1.6 + 0.2, 0, 1)
    vent = block(0.1, 0.5, 0.16, 0.4)
    col[vent & ((XX[sl] % 6) < 3)] = np.array([0.05, 0.05, 0.06])
    scuffs(col, 40, 71, dark=0.75, length=(3, 14))
    img[sl] = col

    # ---- boots: u 0 = top of the foot, 0.5 = sole; v 0 = heel, 1 = toe
    sl, u, v, w, h = grid("boot")
    col = np.array([0.66, 0.66, 0.64])[None, None, :] * (weave[sl] * (0.92 + 0.14 * fbm(u * 5, v * 5, 73, px=5)))[..., None]
    rubber = np.array([0.075, 0.075, 0.08])
    under = np.clip(1 - np.abs(u - 0.5) / 0.19, 0, 1)
    sole = (under > 0) | (v > 0.84) | (v < 0.1)
    col[sole] = (rubber[None, None, :] * (0.85 + 0.4 * grain[sl])[..., None])[sole]
    tread = (under > 0.25) & ((YY[sl] % 9) < 3)
    col[tread] *= 0.45
    welt = (np.abs(np.abs(u - 0.5) - 0.2) * w < 1.6) & (v > 0.1) & (v < 0.84)
    col[welt] = np.array([0.3, 0.3, 0.31])
    for at in (0.3, 0.42):                                               # strap across the instep
        strap = (np.abs(v - at) * h < 5) & ~sole
        col[strap] = np.array([0.24, 0.25, 0.27])
    grime = np.clip(fbm(u * 5, v * 5, 75, px=5) * 2.0 - 0.75, 0, 1) * 0.75
    col = col * (1 - grime[..., None]) + GRIME * 0.8 * grime[..., None]
    scuffs(col, 150, 77, dark=0.6, length=(4, 20))
    img[sl] = col

    # ---- small swatches
    sl, u, v, w, h = grid("steel")
    brushed = fbm(u * 3, v * 70, 81, px=3)
    img[sl] = np.array([0.57, 0.59, 0.62])[None, None, :] * (0.82 + 0.3 * brushed + 0.05 * (grain[sl] - 0.5) - 0.2 * (np.abs(v - 0.5) > 0.44))[..., None]
    sl, u, v, w, h = grid("rubber")
    img[sl] = np.array([0.06, 0.06, 0.065])[None, None, :] * (0.8 + 0.5 * (np.sin(v * h * math.pi / 3.0) > 0) + 0.3 * grain[sl])[..., None]
    sl, u, v, w, h = grid("hose")
    img[sl] = np.array([0.13, 0.13, 0.14])[None, None, :] * (0.55 + 0.9 * (np.sin(v * h * math.pi / 2.0) * 0.5 + 0.5) + 0.2 * grain[sl])[..., None]
    sl, u, v, w, h = grid("dark")
    img[sl] = np.array([0.1, 0.105, 0.115])[None, None, :] * (0.85 + 0.3 * fbm(u * 8, v * 8, 83))[..., None]
    sl, u, v, w, h = grid("lens")
    lens = np.zeros((h, w, 3))
    lens[:] = np.array([0.12, 0.12, 0.13])
    lit = v > 0.55
    lens[lit] = (np.array([1.0, 0.95, 0.8])[None, None, :] * (0.8 + 0.2 * smoothstep(0.55, 1.0, v))[..., None])[lit]
    img[sl] = lens
    sl, u, v, w, h = grid("tank")
    col = np.array([0.5, 0.56, 0.6])[None, None, :] * (0.88 + 0.2 * fbm(u * 3, v * 30, 85, px=3) + 0.05 * (grain[sl] - 0.5))[..., None]
    for at in (0.3, 0.72):
        col[np.abs(v - at) < 0.03] = np.array([0.16, 0.16, 0.17])
    col[np.abs(v - 0.5) < 0.05] = np.array([0.85, 0.85, 0.82])
    col[(np.abs(v - 0.5) < 0.05) & (((XX[sl] * 2 + YY[sl]) % 12) < 6)] = np.array([0.1, 0.1, 0.1])
    scuffs(col, 30, 87, dark=0.6, length=(3, 12))
    img[sl] = col
    sl, u, v, w, h = grid("pad")
    col = np.array([0.15, 0.15, 0.16])[None, None, :] * (0.8 + 0.45 * (np.sin(v * h * math.pi / 4.0) > 0.2) + 0.25 * grain[sl])[..., None]
    worn = np.clip(1.2 - np.hypot((u - 0.5) * 2.4, (v - 0.5) * 2.4), 0, 1) * np.clip(fbm(u * 8, v * 8, 89) * 2 - 0.6, 0, 1)
    col = col * (1 - 0.6 * worn[..., None]) + np.array([0.4, 0.39, 0.37]) * 0.6 * worn[..., None]
    border = np.minimum(np.minimum(u, 1 - u) * w, np.minimum(v, 1 - v) * h)
    col[border < 6] = np.array([0.09, 0.09, 0.1])
    scuffs(col, 80, 91, dark=0.55, length=(4, 18))
    img[sl] = col

    # ---- contact shadows and dirt gathered in the corners
    shade = np.clip(ao, 0, 1)
    img *= (0.42 + 0.58 * shade)[..., None]
    crevice = np.clip(0.72 - shade, 0, 1)[..., None] * 0.45
    img = img * (1 - crevice) + GRIME * 0.5 * crevice
    # The torso is closed under the collar by a flat liner whose triangles all sample the first rows of the torso's
    # rectangle: keep those rows one plain colour so the wearer, looking down with no helmet drawn, sees a clean neck.
    y0, y1, x0, x1 = rect_px("torso")
    rows = slice(y0, y0 + 10)
    img[rows, x0:x1] = np.array([0.36, 0.37, 0.39])[None, None, :] * weave[rows, x0:x1][..., None]
    return np.clip(img, 0, 1)


# ----------------------------------------------------------------------------- output
def to_unity(v):
    return (v[0], v[2], -v[1])


class ExportPart:
    def __init__(self, name, parent, pivot, material, length=0.0):
        self.name, self.parent, self.pivot, self.material, self.length = name, parent, Vector(pivot), material, length
        self.v, self.n, self.uv, self.t = [], [], [], []

    def add(self, chunk):
        """Welds corners that share a vertex, a normal and a UV."""
        index = {}
        base_keys = np.round(chunk.cn * 500).astype(np.int64)
        uv_keys = np.round(chunk.uv * 8192).astype(np.int64)
        for t in range(len(chunk.tris)):
            tri = []
            for k in range(3):
                key = (int(chunk.tris[t, k]), tuple(base_keys[t, k]), tuple(uv_keys[t, k]))
                i = index.get(key)
                if i is None:
                    i = len(self.v)
                    index[key] = i
                    self.v.append(tuple(chunk.pos[chunk.tris[t, k]]))
                    self.n.append(tuple(chunk.cn[t, k]))
                    self.uv.append(tuple(chunk.uv[t, k]))
                tri.append(i)
            if tri[0] != tri[1] and tri[1] != tri[2] and tri[0] != tri[2]:
                self.t.append(tuple(tri))


def export(parts):
    positions, normals, uvs, indices, meta = [], [], [], [], []
    for p in parts:
        v_start, i_start = len(positions) // 3, len(indices)
        for v, n, uv in zip(p.v, p.n, p.uv):
            x, y, z = to_unity(v)
            nx, ny, nz = to_unity(n)
            if p.length > 0:                                  # arm segments: unit length along Unity +Z
                z /= p.length
                nz *= p.length
                k = math.sqrt(nx * nx + ny * ny + nz * nz) or 1.0
                nx, ny, nz = nx / k, ny / k, nz / k
            positions.extend((x, y, z))
            normals.extend((nx, ny, nz))
            uvs.extend(uv)
        for tri in p.t:
            indices.extend(tri)
        meta.append(dict(name=p.name, parent=p.parent or "", pivot=list(to_unity(p.pivot)), material=p.material, length=p.length,
                         vertexStart=v_start, vertexCount=len(p.v), indexStart=i_start, indexCount=len(p.t) * 3, triangles=len(p.t)))
    with open(os.path.join(OUT, MODEL_ID + ".mesh.bin"), "wb") as f:
        f.write(struct.pack("<%df" % len(positions), *positions))
        f.write(struct.pack("<%df" % len(normals), *normals))
        f.write(struct.pack("<%df" % len(uvs), *uvs))
        f.write(struct.pack("<%dI" % len(indices), *indices))
    arm = sum(len(p.t) for p in parts if p.name.startswith(("upperarm", "forearm")))
    info = dict(id=MODEL_ID, smoothness=SMOOTHNESS, vertexTotal=len(positions) // 3, indexTotal=len(indices),
                triangles=len(indices) // 3, trianglesWithBothArms=len(indices) // 3 + arm,
                rig=dict(headCentre=list(EYES_TO_HEAD_CENTRE), neck=[0.0, -NECK_DROP, 0.0], shoulder=[SHOULDER_X, SHOULDER_UP, 0.0],
                         hips=[0.0, -HIP_DROP, 0.0], upperArmLength=UPPER_ARM, forearmLength=FOREARM),
                parts=meta)
    with open(os.path.join(OUT, MODEL_ID + ".mesh.json"), "w") as f:
        json.dump(info, f, indent=1)
    return info


def solve_arm(shoulder, target, pole):
    """The same two-bone solve the game uses: returns the elbow and wrist."""
    to = target - shoulder
    reach = min(max(to.length, abs(UPPER_ARM - FOREARM) + 1e-3), UPPER_ARM + FOREARM - 1e-3)
    d = to.normalized()
    x = (UPPER_ARM ** 2 - FOREARM ** 2 + reach ** 2) / (2 * reach)
    h = math.sqrt(max(UPPER_ARM ** 2 - x ** 2, 0.0))
    bend = (pole - d * pole.dot(d)).normalized()
    return shoulder + d * x + bend * h, shoulder + d * reach, bend


def segment_matrix(start, end, bend, mirror):
    along = (end - start).normalized()
    up = (-bend - along * (-bend).dot(along)).normalized()
    y = -along
    x = y.cross(up)
    if mirror:
        x = -x
    m = Matrix((x, y, up)).transposed().to_4x4()
    m.translation = start
    return m


def assemble(parts):
    """The finished suit as ordinary Blender objects, posed, sharing the atlas."""
    image = bpy.data.images.load(os.path.join(OUT, MODEL_ID + "_atlas.png"))
    materials = {}
    for key, tint, rough in (("suit", None, 0.75), ("accent", (0.9, 0.4, 0.1, 1.0), 0.75), ("visor", None, 0.08)):
        material = bpy.data.materials.new(MODEL_ID + "_" + key)
        try:
            material.use_nodes = True
        except Exception:
            pass
        tree = material.node_tree
        bsdf = tree.nodes.get("Principled BSDF")
        tex = tree.nodes.new("ShaderNodeTexImage")
        tex.image = image
        if bsdf is not None:
            bsdf.inputs["Roughness"].default_value = rough
            if tint is None:
                tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
            else:
                try:
                    mix = tree.nodes.new("ShaderNodeMix")
                    mix.data_type = "RGBA"
                    mix.blend_type = "MULTIPLY"
                    mix.inputs[0].default_value = 1.0
                    tree.links.new(tex.outputs["Color"], mix.inputs[6])
                    mix.inputs[7].default_value = tint
                    tree.links.new(mix.outputs[2], bsdf.inputs["Base Color"])
                except Exception:
                    bsdf.inputs["Base Color"].default_value = tint
        materials[key] = material

    suit = bpy.data.collections.new("Astronaut")
    bpy.context.scene.collection.children.link(suit)
    stand_ins = bpy.data.collections.new("Stand-ins (not exported)")
    bpy.context.scene.collection.children.link(stand_ins)
    materials["glove"] = bpy.data.materials.new("glove_stand_in")
    materials["glove"].diffuse_color = (0.8, 0.8, 0.76, 1)
    try:
        materials["glove"].use_nodes = True
        materials["glove"].node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.62, 0.62, 0.58, 1)
    except Exception:
        pass
    meshes = {}
    for p in parts:
        mesh = bpy.data.meshes.new(p.name)
        mesh.from_pydata([tuple(v) for v in p.v], [], p.t)
        layer = mesh.uv_layers.new(name="UVMap")
        for loop in mesh.loops:
            layer.data[loop.index].uv = p.uv[loop.vertex_index]
        mesh.materials.append(materials[p.material])
        for poly in mesh.polygons:
            poly.use_smooth = True
        mesh.update()
        try:
            mesh.normals_split_custom_set_from_vertices([tuple(n) for n in p.n])
        except Exception:
            pass
        meshes[p.name] = mesh

    head = Vector((0, 0, 1.63))
    torso = head + Vector((0, 0, -NECK_DROP))
    placed = {"helmet": Matrix.Translation(head) @ Matrix.Rotation(math.radians(-6), 4, "X") @ Matrix.Rotation(math.radians(8), 4, "Z"),
              "torso": Matrix.Translation(torso), "legs": Matrix.Translation(torso + Vector((0, 0, -HIP_DROP)))}
    objects = {}

    def place(name, mesh_name, matrix, parent=None):
        obj = bpy.data.objects.new(name, meshes[mesh_name])
        suit.objects.link(obj)
        if parent is not None:
            obj.parent = objects[parent]
        else:
            obj.matrix_world = matrix
        objects[name] = obj
        return obj
    for p in parts:
        if p.name in placed:
            place(p.name, p.name, placed[p.name])
    for p in parts:
        if p.parent in placed:
            place(p.name, p.name, None, p.parent)
    for side, label, hand in ((1, "R", P(0.3, 0.33, -0.2)), (-1, "L", P(-0.36, 0.2, -0.43))):
        shoulder = torso + P(side * SHOULDER_X, 0, SHOULDER_UP)
        elbow, wrist, bend = solve_arm(shoulder, torso + hand, P(side * 0.75, -0.3, -1.0))
        place("upperarm_" + label, "upperarm", segment_matrix(shoulder, elbow, bend, side < 0))
        place("upperarm_accent_" + label, "upperarm_accent", None, "upperarm_" + label)
        place("forearm_" + label, "forearm", segment_matrix(elbow, wrist, bend, side < 0))
        # The game has its own articulated gloves (Builder.Prefabs.cs BuildHand). This block only stands in for one in the stills.
        bm = bmesh.new()
        bmesh.ops.create_cube(bm, size=1.0)
        for v in bm.verts:
            v.co = Vector((v.co.x * 0.09, v.co.y * 0.17 - 0.085, v.co.z * 0.045))
        mesh = bpy.data.meshes.new("glove_stand_in")
        bm.to_mesh(mesh)
        bm.free()
        glove = bpy.data.objects.new("glove_stand_in_" + label + " (not exported)", mesh)
        stand_ins.objects.link(glove)
        glove.matrix_world = segment_matrix(wrist, wrist + (wrist - elbow), bend, side < 0)
        bevel = glove.modifiers.new("Bevel", "BEVEL")
        bevel.width, bevel.segments = 0.018, 3
        mesh.materials.append(materials["glove"])
    return suit


def render(folder):
    os.makedirs(folder, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 40
    try:
        scene.cycles.use_denoising = True
    except Exception:
        pass
    scene.render.resolution_x, scene.render.resolution_y = 900, 1200
    scene.render.image_settings.file_format = "PNG"
    try:
        scene.view_settings.view_transform = "Standard"
    except Exception:
        pass
    world = bpy.data.worlds.new("Stage")
    scene.world = world
    try:
        world.use_nodes = True
    except Exception:
        pass
    background = world.node_tree.nodes.get("Background")
    if background is not None:
        background.inputs[0].default_value = (0.09, 0.1, 0.12, 1)
        background.inputs[1].default_value = 0.6

    def light(name, location, energy, size, colour=(1, 1, 1)):
        data = bpy.data.lights.new(name, "AREA")
        data.energy, data.size, data.color = energy, size, colour
        obj = bpy.data.objects.new(name, data)
        scene.collection.objects.link(obj)
        obj.location = location
        obj.rotation_euler = (Vector((0, 0, 1.1)) - Vector(location)).to_track_quat("-Z", "Y").to_euler()
    light("Key", P(1.8, 2.6, 2.9), 300, 2.0, (1.0, 0.97, 0.92))
    light("Fill", P(-2.6, 1.6, 1.2), 70, 3.0, (0.8, 0.88, 1.0))
    light("Rim", P(-1.2, -2.8, 2.6), 240, 2.0)
    light("Rim2", P(2.2, -2.2, 0.8), 110, 2.0)
    cam_data = bpy.data.cameras.new("Camera")
    cam = bpy.data.objects.new("Camera", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    shots = [("front", P(0.9, 3.1, 1.35), (0, 0, 0.98), 50), ("back", P(-1.3, -2.9, 1.5), (0, 0, 0.98), 50), ("side", P(3.2, 0.3, 1.2), (0, 0, 0.98), 50),
             ("bust", P(-0.55, 1.25, 1.55), (0, 0, 1.42), 50), ("top", P(0.25, 0.9, 2.6), (0, 0, 1.3), 50), ("legs", P(0.9, 1.5, 0.6), (0, 0, 0.55), 50)]
    for name, location, target, lens in shots:
        cam.location = location
        cam.rotation_euler = (Vector(target) - Vector(location)).to_track_quat("-Z", "Y").to_euler()
        cam_data.lens = lens
        scene.render.filepath = os.path.join(folder, "suit_" + name + ".png")
        bpy.ops.render.render(write_still=True)


def main():
    global CAGES
    started = time.time()
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    CAGES = bpy.data.collections.new("Modelling cages (modifiers live)")
    bpy.context.scene.collection.children.link(CAGES)
    make_folds()

    helmet, helmet_split = build_helmet()
    torso, torso_split = build_torso()
    upper, upper_split, fore = build_arm()
    legs, legs_split = build_legs()
    collar = [c for c in torso if c.name.startswith("torso_neck")]
    torso = [c for c in torso if not c.name.startswith("torso_neck")]
    groups = dict(helmet=helmet, torso=torso, collar=collar, upperarm=upper, forearm=fore, legs=legs)
    for chunks in groups.values():
        for chunk in chunks:
            if chunk.cn is None:
                chunk.normals()
        bake_occlusion(chunks)
    ao = raster_occlusion([c for chunks in groups.values() for c in chunks])

    # split the crew-colour pieces and the visor off the surfaces they were modelled as part of
    shell = helmet[0]
    extra = dict(helmet_visor=[], helmet_accent=[], torso_accent=[], upperarm_accent=[], legs_accent=[])
    for mask, name, material in helmet_split:
        extra["helmet_visor" if material == "visor" else "helmet_accent"].append((shell, mask, name, material))
    extra["torso_accent"] = torso_split
    extra["upperarm_accent"] = upper_split
    extra["legs_accent"] = legs_split
    pieces = {}
    for part_name, entries in extra.items():
        by_chunk = {}
        for chunk, mask, name, material in entries:
            by_chunk.setdefault(id(chunk), (chunk, []))[1].append((mask, name, material))
        pieces[part_name] = []
        for chunk, items in by_chunk.values():
            # masks were made against the unsplit chunk: cut them all before any triangle is removed
            cuts = [Chunk(name, chunk.pos, chunk.tris[mask], chunk.uv[mask], chunk.region, material, chunk.angle, chunk.bake[mask]) for mask, name, material in items]
            for cut, (mask, name, material) in zip(cuts, items):
                cut.cn = chunk.cn[mask]
                pieces[part_name].append(cut)
    # remove the split triangles from their source chunks (all masks for a chunk together)
    removal = {}
    for entries in extra.values():
        for chunk, mask, name, material in entries:
            removal[id(chunk)] = (chunk, removal.get(id(chunk), (chunk, np.zeros(len(mask), dtype=bool)))[1] | mask)
    for chunk, mask in removal.values():
        keep = ~mask
        chunk.tris, chunk.uv, chunk.bake, chunk.cn = chunk.tris[keep], chunk.uv[keep], chunk.bake[keep], chunk.cn[keep]

    head = Vector((0, 0, 0))
    neck = head + Vector((0, 0, -NECK_DROP))
    shoulder = neck + P(SHOULDER_X, 0, SHOULDER_UP)
    layout = [
        ("helmet", "", head, "suit", helmet, 0.0), ("helmet_visor", "helmet", head, "visor", pieces["helmet_visor"], 0.0),
        ("helmet_accent", "helmet", head, "accent", pieces["helmet_accent"], 0.0),
        ("torso", "", neck, "suit", torso, 0.0), ("torso_accent", "torso", neck, "accent", pieces["torso_accent"], 0.0),
        ("torso_collar", "torso", neck, "suit", collar, 0.0),
        ("upperarm", "", shoulder, "suit", upper, UPPER_ARM), ("upperarm_accent", "upperarm", shoulder, "accent", pieces["upperarm_accent"], UPPER_ARM),
        ("forearm", "", shoulder + Vector((0, 0, -UPPER_ARM)), "suit", fore, FOREARM),
        ("legs", "", neck + Vector((0, 0, -HIP_DROP)), "suit", legs, 0.0),
        ("legs_accent", "legs", neck + Vector((0, 0, -HIP_DROP)), "accent", pieces["legs_accent"], 0.0),
    ]
    parts = []
    for name, parent, pivot, material, chunks, length in layout:
        part = ExportPart(name, parent, pivot, material, length)
        for chunk in chunks:
            part.add(chunk)
        parts.append(part)

    atlas = paint_atlas(ao)
    rgba = np.ones((SIZE, SIZE, 4), dtype=np.float32)
    rgba[..., :3] = atlas
    image = bpy.data.images.new(MODEL_ID + "_atlas", SIZE, SIZE, alpha=False)
    image.pixels.foreach_set(rgba.ravel())
    image.filepath_raw = os.path.join(OUT, MODEL_ID + "_atlas.png")
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)

    info = export(parts)
    assemble(parts)
    CAGES.hide_render = True
    CAGES.hide_viewport = True
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if "--render" in args:
        render(args[args.index("--render") + 1])
    for img in bpy.data.images:
        if img.filepath and not img.filepath.startswith("//") and os.path.dirname(bpy.path.abspath(img.filepath)) == OUT:
            img.filepath = "//" + os.path.basename(img.filepath)
    bpy.context.preferences.filepaths.save_version = 0       # no astronaut.blend1 backup beside the model
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, MODEL_ID + ".blend"), compress=True)
    for p in info["parts"]:
        print("PART %-16s %5d triangles %5d vertices  material %s" % (p["name"], p["triangles"], p["vertexCount"], p["material"]))
    print("ASTRONAUT: %d parts, %d triangles in the file, %d in game with both arms, %d vertices, %.1f s"
          % (len(parts), info["triangles"], info["trianglesWithBothArms"], info["vertexTotal"], time.time() - started))


if __name__ == "__main__":
    main()
