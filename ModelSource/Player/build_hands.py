"""
EVA glove for Trash Astronauts (the player's hands), modelled to match the suit from build_player.py.

Run:  Blender -b --factory-startup -P build_hands.py [-- --render <folder>]

Writes next to this file:
    glove.mesh.json    part list (name, parent, pivot, rest rotation, length, ranges into the .bin)
    glove.mesh.bin     float32 positions, normals, uvs; uint32 indices (Unity axes, each part in its own space)
    glove_atlas.png    the one 512 x 512 colour texture every part uses
    glove.blend        the assembled right glove (open hand), for looking at

Only the RIGHT glove is modelled. The game mirrors it for the left hand with a negative x scale on the
model's holder, which Unity renders with the winding flipped, so the left glove is not inside out.

Coordinates are the game's glove space (Builder.Prefabs.cs BuildHand), in Unity axes: palm facing -Y,
fingers along +Z, the right thumb on -X. The palm centre is (0, -0.03, -0.075); the suit forearm meets the
cuff at (0, -0.03, -0.18). Every finger segment is its own rigid part with its pivot at its knuckle, so the
game curls it by rotating about its local +X (which swings the segment's +Z toward its -Y, the palm side).
Segment ends are rounded and overlap the next segment, so the joints stay closed when they bend.
"""
import bpy, json, math, os, struct, sys, time
import numpy as np

OUT = os.path.dirname(os.path.abspath(__file__))
MODEL_ID = "glove"
SIZE = 512
SMOOTHNESS = 0.25
RNG = np.random.default_rng(7)

# ----------------------------------------------------------------------------- atlas layout (u0, u1, v0, v1)
REGIONS = {
    "fabric":  (0.00, 0.50, 0.50, 1.00),   # white suit fabric with folds, seams, grime
    "rubber":  (0.50, 0.75, 0.50, 1.00),   # grey rubberised fingertip caps and finger pads
    "metal":   (0.75, 1.00, 0.50, 1.00),   # wrist bearing ring
    "palm":    (0.00, 0.50, 0.00, 0.50),   # palm, planar: fabric with two rubber grip pads
    "back":    (0.50, 1.00, 0.00, 0.25),   # back of the hand, planar: fabric with a rigid shell plate
    "orange":  (0.50, 0.75, 0.25, 0.50),   # orange accent band on the gauntlet
    "knuckle": (0.75, 1.00, 0.25, 0.50),   # ridged rubber knuckle guard
}
INSET = 3.0 / SIZE


def to_atlas(region, uv):
    u0, u1, v0, v1 = REGIONS[region]
    u = u0 + INSET + np.clip(uv[..., 0], 0, 1) * (u1 - u0 - 2 * INSET)
    v = v0 + INSET + np.clip(uv[..., 1], 0, 1) * (v1 - v0 - 2 * INSET)
    return np.stack([u, v], axis=-1)


# ----------------------------------------------------------------------------- geometry
class Chunk:
    """Positions, triangles, a local 0..1 UV per vertex and an atlas region per triangle."""

    def __init__(self, pos, tris, luv, region):
        self.pos = np.asarray(pos, dtype=np.float64)
        self.tris = np.asarray(tris, dtype=np.int64).reshape(-1, 3)
        self.luv = np.asarray(luv, dtype=np.float64)
        self.region = [region] * len(self.tris) if isinstance(region, str) else list(region)

    def face_normals(self):
        p = self.pos[self.tris]
        return np.cross(p[:, 1] - p[:, 0], p[:, 2] - p[:, 0])

    def drop_degenerate(self):
        area = np.linalg.norm(self.face_normals(), axis=1)
        keep = area > 1e-12
        self.tris = self.tris[keep]
        self.region = [r for r, k in zip(self.region, keep) if k]

    def orient_outward(self, centre=None):
        """Flips the winding if most of the surface faces inward (so normals point out of the solid)."""
        n = self.face_normals()
        c = self.pos[self.tris].mean(axis=1)
        ref = self.pos.mean(axis=0) if centre is None else centre
        if np.sum(np.einsum("ij,ij->i", n, c - ref)) < 0:
            self.tris = self.tris[:, ::-1].copy()

    def vertex_normals(self):
        fn = self.face_normals()
        vn = np.zeros_like(self.pos)
        for k in range(3):
            np.add.at(vn, self.tris[:, k], fn)
        keys = np.round(self.pos * 1e5).astype(np.int64)
        _, inverse = np.unique(keys, axis=0, return_inverse=True)
        inverse = inverse.reshape(-1)
        welded = np.zeros((inverse.max() + 1, 3))
        np.add.at(welded, inverse, vn)
        vn = welded[inverse]
        return vn / np.maximum(np.linalg.norm(vn, axis=1, keepdims=True), 1e-12)


def grid_tris(rings, n):
    """Quads between consecutive rings of n + 1 vertices (the last column repeats the first for the UV seam)."""
    tris = []
    for k in range(rings - 1):
        for j in range(n):
            a, b = k * (n + 1) + j, k * (n + 1) + j + 1
            c, d = (k + 1) * (n + 1) + j + 1, (k + 1) * (n + 1) + j
            tris += [(a, b, c), (a, c, d)]
    return np.array(tris)


def superellipse(theta, p):
    c, s = np.cos(theta), np.sin(theta)
    return np.sign(c) * np.abs(c) ** (2.0 / p), np.sign(s) * np.abs(s) ** (2.0 / p)


def capsule(z0, z1, w0, h0, w1, h1, n=10, rings=10, p=2.6, e=0.55, pad=0.0, swell=None):
    """
    A closed, rounded, slightly boxy finger-like solid along +Z from z0 to z1. Cross sections are superellipses
    of half width w and half height h (blended from the z0 end to the z1 end). pad > 0 fattens the -Y (palm) side.
    swell(t) multiplies the section size along the length (t = 0..1), for bellows and pads.
    """
    phi = np.linspace(0.0, math.pi, rings)
    c, s = np.cos(phi), np.sin(phi)
    zc, hz = 0.5 * (z0 + z1), 0.5 * (z1 - z0)
    z = zc - hz * np.sign(c) * np.abs(c) ** e
    rho = np.abs(s) ** e
    t = (z - z0) / (z1 - z0)
    theta = np.linspace(0.0, 2 * math.pi, n + 1) - math.pi / 2  # seam on the palm side
    sx, sy = superellipse(theta, p)
    pos, luv = [], []
    for k in range(rings):
        f = rho[k] * (swell(t[k]) if swell is not None else 1.0)
        w = (w0 + (w1 - w0) * t[k]) * f
        h = (h0 + (h1 - h0) * t[k]) * f
        for j in range(n + 1):
            y = h * sy[j]
            if y < 0:
                y *= 1.0 + pad
            pos.append((w * sx[j], y, z[k]))
            luv.append((j / n, t[k]))
    return np.array(pos), grid_tris(rings, n), np.array(luv)


def lathe(profile, n, centre, sx=1.0, sy=1.0):
    """An open tube along +Z through rings (z, rx, ry, region-of-band-after-this-ring)."""
    theta = np.linspace(0.0, 2 * math.pi, n + 1) - math.pi / 2
    pos, luv = [], []
    zs = [r[0] for r in profile]
    for k, (z, rx, ry, _) in enumerate(profile):
        for j in range(n + 1):
            pos.append((centre[0] + rx * math.cos(theta[j]), centre[1] + ry * math.sin(theta[j]), z))
            luv.append((j / n, (z - zs[0]) / (zs[-1] - zs[0])))
    tris = grid_tris(len(profile), n)
    region = [profile[k][3] for k in range(len(profile) - 1) for _ in range(2 * n)]
    return np.array(pos), tris, np.array(luv), region


def gauss(x):
    return np.exp(-x * x)


# ----------------------------------------------------------------------------- hand layout (right hand)
PALM_CENTRE = np.array([0.0, -0.03, -0.075])
WRIST = np.array([0.0, -0.03, -0.18])
FINGERS = ["index", "middle", "ring", "little"]
FINGER_X = [-0.0305, -0.0100, 0.0105, 0.0300]          # right hand: index on the thumb side (-X)
FINGER_Z = [-0.025, -0.021, -0.024, -0.031]            # knuckle arch
FINGER_Y = -0.029
FINGER_LEN = [(0.040, 0.026, 0.022), (0.044, 0.028, 0.023), (0.041, 0.027, 0.022), (0.032, 0.021, 0.019)]
FINGER_W = [0.0099, 0.0102, 0.0096, 0.0086]           # half widths of the proximal segment (gloved)
FINGER_SPLAY = [-6.0, -1.0, 4.5, 11.0]                  # degrees about Y at the knuckle
THUMB_BASE = np.array([-0.031, -0.043, -0.112])
THUMB_DIRS = [(-0.72, -0.22, 0.66), (-0.56, -0.24, 0.79), (-0.44, -0.26, 0.86)]   # ~45 degrees out
THUMB_LEN = (0.042, 0.032, 0.027)
THUMB_W = (0.0150, 0.0130, 0.0118)


def normalize(v):
    v = np.asarray(v, dtype=np.float64)
    return v / np.linalg.norm(v)


def frame(z_dir, flex_dir):
    """Rotation matrix (columns x, y, z) with +Z along the segment and -Y toward where it curls."""
    z = normalize(z_dir)
    f = np.asarray(flex_dir, dtype=np.float64)
    f = normalize(f - z * f.dot(z))
    y = -f
    x = np.cross(y, z)
    return np.stack([x, y, z], axis=1)


def rot_y(deg):
    a = math.radians(deg)
    return np.array([[math.cos(a), 0, math.sin(a)], [0, 1, 0], [-math.sin(a), 0, math.cos(a)]])


def rot_x(deg):
    a = math.radians(deg)
    return np.array([[1, 0, 0], [0, math.cos(a), -math.sin(a)], [0, math.sin(a), math.cos(a)]])


def mat_to_quat(m):
    """Unity order (x, y, z, w)."""
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        w, x, y, z = 0.25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w, x, y, z = (m[2, 1] - m[1, 2]) / s, 0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w, x, y, z = (m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w, x, y, z = (m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s
    return [float(x), float(y), float(z), float(w)]


class Part:
    def __init__(self, name, parent, pivot, rotation, length, chunks):
        self.name, self.parent, self.length = name, parent, length
        self.pivot = np.asarray(pivot, dtype=np.float64)        # in the parent's space
        self.rotation = np.asarray(rotation, dtype=np.float64)  # rest rotation in the parent's space
        self.chunks = chunks


def smooth_box(v, lo, hi, soft):
    """1 inside [lo, hi], easing to 0 over `soft` outside it."""
    return np.clip((v - lo) / soft + 1, 0, 1) * np.clip((hi - v) / soft + 1, 0, 1)


def build_palm():
    # Palm and back of the hand: an organic block, narrower and thinner at the wrist, round-edged, arched
    # across the back, with a padded heel, thumb mound and finger-root pads underneath and a knuckle ridge on top.
    pos, tris, luv = capsule(-0.134, -0.017, 0.037, 0.0138, 0.0472, 0.0162, n=18, rings=12, p=2.35, e=0.5)
    pos = pos.copy()
    x, y, z = pos[:, 0], pos[:, 1], pos[:, 2]
    under = np.clip(-y / 0.015, 0, 1)
    over = np.clip(y / 0.015, 0, 1)
    heel = 0.006 * gauss((z + 0.112) / 0.018) * (0.6 + 0.4 * gauss(x / 0.03))
    thenar = 0.0062 * gauss((z + 0.094) / 0.022) * gauss((x + 0.027) / 0.014)
    hypothenar = 0.003 * gauss((z + 0.09) / 0.025) * gauss((x - 0.031) / 0.012)
    hollow = -0.0025 * gauss((z + 0.07) / 0.015) * gauss(x / 0.018)
    pads = 0.0025 * gauss((z + 0.034) / 0.009) * gauss(x / 0.04)
    y -= under * (heel + thenar + hypothenar + hollow + pads)
    # Arched across the back (the metacarpals), a knuckle ridge with a bump over each knuckle, and the back
    # plate as a thin pad (about 1.5 mm) that follows the curve.
    arch = 0.0045 * np.clip(1 - (x / 0.048) ** 2, 0, 1)
    ridge = 0.0026 * gauss((z + 0.03) / 0.009) * gauss(x / 0.042)
    for fx in (-0.0305, -0.010, 0.0105, 0.030):
        ridge += 0.0016 * gauss((z + 0.027) / 0.007) * gauss((x - fx) / 0.006)
    plate = 0.0015 * smooth_box(x, -0.027, 0.027, 0.003) * smooth_box(z, -0.108, -0.043, 0.003)
    y += over * (ridge + plate) + arch
    pos[:, 1] = y + PALM_CENTRE[1]
    palm_luv = np.stack([(x + 0.05) / 0.10, (z + 0.135) / 0.12], axis=1)
    chunk = Chunk(pos, tris, luv, "fabric")
    chunk.drop_degenerate()
    chunk.orient_outward()
    fn = chunk.face_normals()
    fn /= np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-12)
    chunk.region = ["palm" if n[1] < -0.5 else "back" if n[1] > 0.5 else "fabric" for n in fn]
    planar = [r in ("palm", "back") for r in chunk.region]
    chunk.planar_luv = palm_luv
    chunk.planar = planar

    # Gauntlet: tapers smoothly out of the palm into the cuff (one smooth surface).
    gauntlet = [
        (-0.110, 0.0335, 0.0125, "fabric"),
        (-0.121, 0.0385, 0.0175, "fabric"),
        (-0.131, 0.0435, 0.0240, "fabric"),
        (-0.140, 0.0470, 0.0290, "orange"),
        (-0.146, 0.0485, 0.0315, "orange"),
        (-0.153, 0.0495, 0.0328, "fabric"),
        (-0.1605, 0.0502, 0.0336, "fabric"),
    ]
    # The rigid wrist ring: its own surface, so its edges stay crisp (split normals at the hard edge).
    ring = [
        (-0.1595, 0.0500, 0.0334, "metal"),
        (-0.1615, 0.0545, 0.0380, "metal"),
        (-0.1645, 0.0560, 0.0395, "metal"),
        (-0.1775, 0.0560, 0.0395, "metal"),
        (-0.1810, 0.0540, 0.0375, "metal"),
        (-0.1840, 0.0470, 0.0310, "metal"),
    ]
    out = [chunk]
    for profile in (gauntlet, ring):
        cp, ct, cl, cr = lathe(profile, 20, (0.0, PALM_CENTRE[1]))
        cuff = Chunk(cp, ct, cl, cr)
        fn = cuff.face_normals()
        cc = cuff.pos[cuff.tris].mean(axis=1)
        radial = cc - np.array([0.0, PALM_CENTRE[1], 0.0])
        radial[:, 2] = 0
        if np.sum(np.einsum("ij,ij->i", fn, radial)) < 0:
            cuff.tris = cuff.tris[:, ::-1].copy()
        out.append(cuff)
    return out


def finger_segment(L, w, distal, proximal_bellows, flesh=0.0, thumb=False):
    """
    One rigid phalanx: a round section (12 sides, a little flatter on the pad side) tapering toward the tip,
    bulging at the knuckle it starts from and narrowing at its waist. Both ends are rounded and overlap the
    neighbouring segment, so the joint stays closed when it bends; a distal segment ends in a domed tip.
    """
    h = w * (0.94 if not thumb else 0.9)
    taper = 0.80 if distal else 0.88
    w1, h1 = w * taper, h * taper
    z0 = -0.6 * w
    z1 = L + (0.35 * w if distal else 0.6 * w)

    def swell(t):
        s = 1.0 + 0.07 * gauss((t - 0.10) / 0.10)        # knuckle bulge (gloved joint) at the root
        s -= 0.06 * gauss((t - 0.52) / 0.16)             # narrower waist between knuckles
        if not distal:
            s += 0.03 * gauss((t - 0.92) / 0.08)         # the next knuckle starts to swell
        if proximal_bellows:
            s += 0.03 * gauss((t - 0.2) / 0.05)          # fabric bellows ring over the knuckle
        return s

    pos, tris, luv = capsule(z0, z1, w, h, w1, h1, n=12, rings=11, p=2.0, e=0.72 if distal else 0.62, pad=-0.12 + flesh, swell=swell)
    chunk = Chunk(pos, tris, luv, "fabric")
    chunk.drop_degenerate()
    chunk.orient_outward()
    fn = chunk.face_normals()
    fn /= np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-12)
    # Region by ring band (the lower ring of each quad), so the cap edges run round the finger, not zig-zag.
    zc = chunk.pos[chunk.tris][:, :, 2].min(axis=1)
    regions = []
    for n_, zf in zip(fn, zc):
        if distal and zf > 0.36 * L:
            regions.append("rubber")                     # rubberised fingertip cap
        elif (not thumb) and (not proximal_bellows) and n_[1] < -0.55:
            regions.append("rubber")                     # grip pad under the proximal phalanx
        elif distal and n_[1] < -0.5:
            regions.append("rubber")
        else:
            regions.append("fabric")
    chunk.region = regions
    return chunk


def build_parts():
    parts = [Part("palm", "", np.zeros(3), np.eye(3), 0.0, build_palm())]
    for i, name in enumerate(FINGERS):
        parent = "palm"
        pivot = np.array([FINGER_X[i], FINGER_Y, FINGER_Z[i]])
        rotation = rot_y(FINGER_SPLAY[i])
        for k in range(3):
            L = FINGER_LEN[i][k]
            w = FINGER_W[i] * (1.0, 0.93, 0.87)[k]
            chunk = finger_segment(L, w, distal=(k == 2), proximal_bellows=(k > 0), flesh=0.04)
            seg = "%s_%d" % (name, k + 1)
            parts.append(Part(seg, parent, pivot, rotation, L, [chunk]))
            parent, pivot, rotation = seg, np.array([0.0, 0.0, L]), np.eye(3)
    # Thumb: three segments, each with its own frame (curling across the palm toward the little finger).
    flex = np.array([1.0, -0.4, 0.05])
    worlds = [frame(d, flex) for d in THUMB_DIRS]
    parent, parent_world, pivot = "palm", np.eye(3), THUMB_BASE
    for k in range(3):
        L, w = THUMB_LEN[k], THUMB_W[k]
        chunk = finger_segment(L, w, distal=(k == 2), proximal_bellows=(k > 0), flesh=0.10, thumb=True)
        rotation = parent_world.T @ worlds[k]
        seg = "thumb_%d" % (k + 1)
        parts.append(Part(seg, parent, pivot, rotation, L, [chunk]))
        parent, parent_world, pivot = seg, worlds[k], np.array([0.0, 0.0, L])
    return parts


# ----------------------------------------------------------------------------- posing (same rule as GloveVisual)
REST = {"finger": (10.0, 14.0, 8.0), "thumb": (-6.0, 6.0, 6.0)}
FIST = {"finger": (78.0, 98.0, 62.0), "thumb": (34.0, 40.0, 36.0)}
REST_SPREAD = {"index": 2.0, "middle": 0.0, "ring": -1.0, "little": -3.0}


def pose_curls(amount):
    curls = {}
    for name in FINGERS:
        for k in range(3):
            curls["%s_%d" % (name, k + 1)] = REST["finger"][k] + amount * (FIST["finger"][k] - REST["finger"][k]) * (1.0 + 0.06 * FINGERS.index(name))
    for k in range(3):
        curls["thumb_%d" % (k + 1)] = REST["thumb"][k] + amount * (FIST["thumb"][k] - REST["thumb"][k])
    return curls


def world_transforms(parts, curls):
    out = {}
    for p in parts:
        R_local = p.rotation @ rot_x(curls.get(p.name, 0.0))
        if p.parent:
            pr, pt = out[p.parent]
            out[p.name] = (pr @ R_local, pt + pr @ p.pivot)
        else:
            out[p.name] = (R_local, p.pivot.copy())
    return out


# ----------------------------------------------------------------------------- atlas
def value_noise(h, w, cells, seed):
    rng = np.random.default_rng(seed)
    g = rng.random((cells + 1, cells + 1))
    ys = np.linspace(0, cells, h, endpoint=False)
    xs = np.linspace(0, cells, w, endpoint=False)
    y0, x0 = ys.astype(int), xs.astype(int)
    fy, fx = ys - y0, xs - x0
    fy, fx = fy * fy * (3 - 2 * fy), fx * fx * (3 - 2 * fx)
    a = g[y0][:, x0]
    b = g[y0][:, x0 + 1]
    c = g[y0 + 1][:, x0]
    d = g[y0 + 1][:, x0 + 1]
    top = a + (b - a) * fx[None, :]
    bot = c + (d - c) * fx[None, :]
    return top + (bot - top) * fy[:, None]


def fbm(h, w, seed, base=4, octaves=4):
    out = np.zeros((h, w))
    amp, total = 1.0, 0.0
    for o in range(octaves):
        out += amp * value_noise(h, w, base * 2 ** o, seed + o)
        total += amp
        amp *= 0.5
    return out / total


def paint_atlas():
    img = np.zeros((SIZE, SIZE, 3))

    def region_px(name):
        u0, u1, v0, v1 = REGIONS[name]
        return int(v0 * SIZE), int(v1 * SIZE), int(u0 * SIZE), int(u1 * SIZE)

    def scuffs(tile, count, seed, colour, strength=0.5, length=(3, 14)):
        rng = np.random.default_rng(seed)
        h, w, _ = tile.shape
        for _ in range(count):
            y, x = rng.integers(0, h), rng.integers(0, w)
            ang = rng.random() * math.pi
            L = rng.integers(*length)
            a = strength * (0.3 + 0.7 * rng.random())
            for s in range(L):
                yy = int(y + s * math.sin(ang)) % h
                xx = int(x + s * math.cos(ang)) % w
                tile[yy, xx] = tile[yy, xx] * (1 - a) + np.array(colour) * a

    def fabric_tile(h, w, seed, base, folds=7.0, grime_end=True):
        n = fbm(h, w, seed, 3, 5)
        v = np.linspace(0, 1, h)[:, None]
        u = np.linspace(0, 1, w)[None, :]
        weave = 0.025 * np.sin(u * w * 1.6) * np.sin(v * h * 1.6)
        warp = fbm(h, w, seed + 11, 2, 3)
        fold = 0.5 + 0.5 * np.sin(2 * math.pi * (v * folds + 0.6 * warp))
        crease = np.clip((fold - 0.82) / 0.18, 0, 1) ** 2 * 0.2
        lit = np.clip((0.35 - np.abs(fold - 0.55)) / 0.35, 0, 1) * 0.04
        shade = 0.9 + 0.1 * n + weave - crease + lit
        tile = np.ones((h, w, 3)) * np.array(base)[None, None, :] * shade[..., None]
        grime = fbm(h, w, seed + 5, 2, 4)
        dirt = np.clip(grime - 0.45, 0, 1) * 0.35
        if grime_end:
            dirt += 0.12 * np.clip((v - 0.75) / 0.25, 0, 1)
        tile *= (1 - dirt)[..., None] * np.array([1.0, 0.98, 0.94])[None, None, :] + dirt[..., None] * np.array([0.0, 0.0, 0.0])
        return tile

    def seam_line(tile, axis_pos, vertical=True, dark=0.62):
        h, w, _ = tile.shape
        if vertical:
            x = int(axis_pos * (w - 1))
            tile[:, max(x - 1, 0):x + 1] *= dark
            for yy in range(0, h, 6):
                tile[yy:yy + 3, min(x + 2, w - 1)] *= 0.8
        else:
            y = int(axis_pos * (h - 1))
            tile[max(y - 1, 0):y + 1, :] *= dark
            for xx in range(0, w, 6):
                tile[min(y + 2, h - 1), xx:xx + 3] *= 0.8

    # Fabric (fingers, sides of the hand, gauntlet).
    y0, y1, x0, x1 = region_px("fabric")
    t = fabric_tile(y1 - y0, x1 - x0, 1, (0.86, 0.86, 0.83), folds=6.0)
    seam_line(t, 0.0)
    seam_line(t, 0.5)
    seam_line(t, 1.0)
    scuffs(t, 90, 2, (0.42, 0.4, 0.37), 0.35)
    img[y0:y1, x0:x1] = t

    # Rubber (fingertip caps, finger pads).
    y0, y1, x0, x1 = region_px("rubber")
    h, w = y1 - y0, x1 - x0
    n = fbm(h, w, 21, 6, 4)
    yy, xx = np.mgrid[0:h, 0:w]
    dots = ((yy % 7 < 3) & (xx % 7 < 3)) * 0.06
    t = np.ones((h, w, 3)) * np.array([0.34, 0.345, 0.355]) * (0.88 + 0.16 * n - dots)[..., None]
    wear = np.clip(fbm(h, w, 23, 3, 3) - 0.62, 0, 1) * 1.2
    t = t * (1 - wear[..., None]) + wear[..., None] * np.array([0.5, 0.5, 0.49])
    v = np.linspace(0, 1, h)[:, None, None]
    t *= 1.0 - 0.15 * np.clip((0.45 - v) / 0.1, 0, 1) * 0  # keep flat
    scuffs(t, 60, 24, (0.62, 0.6, 0.57), 0.3)
    img[y0:y1, x0:x1] = t

    # Metal wrist ring: brushed steel, grooves, scratches.
    y0, y1, x0, x1 = region_px("metal")
    h, w = y1 - y0, x1 - x0
    brushed = value_noise(h, w * 1, 64, 31)[:, :]
    streak = fbm(h, 8, 33, 4, 3)[:, :1]
    t = np.ones((h, w, 3)) * np.array([0.63, 0.65, 0.68]) * (0.85 + 0.1 * brushed + 0.1 * streak)[..., None]
    for gv in (0.38, 0.62, 0.9):
        gy = int(gv * h)
        t[max(gy - 2, 0):gy + 2] *= 0.45
    scuffs(t, 80, 34, (0.85, 0.86, 0.88), 0.4)
    scuffs(t, 40, 35, (0.25, 0.24, 0.22), 0.4)
    img[y0:y1, x0:x1] = t

    # Palm: fabric with two dark rubber grip pads (planar UV: u across the hand, thumb side at u = 0; v from wrist to fingers).
    y0, y1, x0, x1 = region_px("palm")
    h, w = y1 - y0, x1 - x0
    t = fabric_tile(h, w, 41, (0.84, 0.84, 0.81), folds=3.0, grime_end=False)
    v = np.linspace(0, 1, h)[:, None]
    u = np.linspace(0, 1, w)[None, :]

    def rounded(u0, u1, v0, v1, r):
        du = np.maximum(np.maximum(u0 + r - u, u - (u1 - r)), 0)
        dv = np.maximum(np.maximum(v0 + r - v, v - (v1 - r)), 0)
        return np.sqrt(du * du + dv * dv) < r

    pad = rounded(0.16, 0.86, 0.12, 0.36, 0.08) | rounded(0.1, 0.92, 0.47, 0.82, 0.07)
    grid = ((np.mod((u * 40 + v * 40), 1.0) < 0.18) | (np.mod((u * 40 - v * 40), 1.0) < 0.18))
    rubber = np.array([0.30, 0.31, 0.32]) * (0.92 + 0.12 * fbm(h, w, 43, 6, 3))[..., None]
    rubber = rubber * (1 - 0.25 * grid[..., None])
    edge = pad & ~(rounded(0.17, 0.85, 0.13, 0.35, 0.075) | rounded(0.11, 0.91, 0.48, 0.81, 0.065))
    t = np.where(pad[..., None], rubber, t)
    t = np.where(edge[..., None], t * 0.6, t)
    scuffs(t, 60, 44, (0.55, 0.53, 0.5), 0.3)
    img[y0:y1, x0:x1] = t

    # Back of the hand: fabric with a rigid white shell plate and an orange band.
    y0, y1, x0, x1 = region_px("back")
    h, w = y1 - y0, x1 - x0
    t = fabric_tile(h, w, 51, (0.86, 0.86, 0.83), folds=4.0, grime_end=False)
    v = np.linspace(0, 1, h)[:, None]
    u = np.linspace(0, 1, w)[None, :]
    plate = rounded(0.2, 0.8, 0.22, 0.78, 0.1)
    shell = np.array([0.92, 0.92, 0.9]) * (0.95 + 0.05 * fbm(h, w, 53, 4, 3))[..., None]
    t = np.where(plate[..., None], shell, t)
    band = plate & (v > 0.36) & (v < 0.46)
    t = np.where(band[..., None], np.array([0.93, 0.42, 0.1]) * (0.92 + 0.08 * fbm(h, w, 55, 5, 3))[..., None], t)
    rim = plate & ~rounded(0.215, 0.785, 0.24, 0.76, 0.09)
    t = np.where(rim[..., None], t * 0.84, t)
    # Ribbed rubber knuckle band over the knuckle ridge (z = -0.03 -> v = 0.875).
    knuckles = rounded(0.07, 0.93, 0.83, 0.93, 0.04)
    ribs = 0.5 + 0.5 * np.cos((v - 0.83) / 0.1 * 2 * math.pi * 3)
    rubber_k = np.array([0.33, 0.34, 0.35]) * (0.8 + 0.22 * ribs + 0.06 * fbm(h, w, 57, 4, 3))[..., None]
    t = np.where(knuckles[..., None], rubber_k, t)
    for ru, rv in ((0.25, 0.28), (0.75, 0.28), (0.25, 0.72), (0.75, 0.72)):
        rivet = (np.abs(u - ru) < 0.012) & (np.abs(v - rv) < 0.04)
        t = np.where(rivet[..., None], np.array([0.55, 0.56, 0.58]), t)
    scuffs(t, 70, 56, (0.4, 0.38, 0.35), 0.35)
    img[y0:y1, x0:x1] = t

    # Orange accent band.
    y0, y1, x0, x1 = region_px("orange")
    t = fabric_tile(y1 - y0, x1 - x0, 61, (0.93, 0.42, 0.1), folds=2.0, grime_end=False)
    seam_line(t, 0.06, vertical=False, dark=0.55)
    seam_line(t, 0.94, vertical=False, dark=0.55)
    scuffs(t, 40, 62, (0.4, 0.25, 0.15), 0.3)
    img[y0:y1, x0:x1] = t

    # Ridged rubber knuckle guard (ridges across its length).
    y0, y1, x0, x1 = region_px("knuckle")
    h, w = y1 - y0, x1 - x0
    v = np.linspace(0, 1, h)[:, None]
    ridge = 0.5 + 0.5 * np.cos(v * 2 * math.pi * 7)
    t = np.ones((h, w, 3)) * np.array([0.33, 0.34, 0.35]) * (0.75 + 0.3 * ridge + 0.08 * fbm(h, w, 71, 4, 3))[..., None]
    scuffs(t, 40, 72, (0.6, 0.58, 0.55), 0.3)
    img[y0:y1, x0:x1] = t
    return np.clip(img, 0, 1)


# ----------------------------------------------------------------------------- export
def part_buffers(part):
    """Splits corners by atlas region; smooth normals across the whole chunk."""
    verts, norms, uvs, tris = [], [], [], []
    for chunk in part.chunks:
        vn = chunk.vertex_normals()
        planar = getattr(chunk, "planar", None)
        index = {}
        for f, tri in enumerate(chunk.tris):
            region = chunk.region[f]
            use_planar = planar is not None and planar[f]
            out = []
            for vi in tri:
                key = (int(vi), region, use_planar)
                i = index.get(key)
                if i is None:
                    i = len(verts)
                    index[key] = i
                    verts.append(chunk.pos[vi])
                    norms.append(vn[vi])
                    luv = chunk.planar_luv[vi] if use_planar else chunk.luv[vi]
                    uvs.append(to_atlas(region, np.asarray(luv)))
                out.append(i)
            tris.append(out)
    return np.array(verts), np.array(norms), np.array(uvs), np.array(tris, dtype=np.int64)


def export(parts):
    positions, normals, uvs, indices, meta = [], [], [], [], []
    vstart, istart = 0, 0
    buffers = {}
    for p in parts:
        v, n, uv, t = part_buffers(p)
        buffers[p.name] = (v, n, uv, t)
        positions.append(v.astype(np.float32))
        normals.append(n.astype(np.float32))
        uvs.append(uv.astype(np.float32))
        indices.append(t.astype(np.uint32).ravel())
        meta.append(dict(name=p.name, parent=p.parent, pivot=[float(c) for c in p.pivot], rotation=mat_to_quat(p.rotation),
                         length=float(p.length), material="glove", vertexStart=vstart, vertexCount=len(v),
                         indexStart=istart, indexCount=int(t.size), triangles=len(t)))
        vstart += len(v)
        istart += int(t.size)
    with open(os.path.join(OUT, MODEL_ID + ".mesh.bin"), "wb") as f:
        for arr in (positions, normals, uvs, indices):
            f.write(np.concatenate(arr).tobytes())
    info = dict(id=MODEL_ID, smoothness=SMOOTHNESS, vertexTotal=vstart, indexTotal=istart, triangles=istart // 3,
                palmCentre=[float(c) for c in PALM_CENTRE], wrist=[float(c) for c in WRIST],
                restCurl=dict(finger=list(REST["finger"]), thumb=list(REST["thumb"])),
                fistCurl=dict(finger=list(FIST["finger"]), thumb=list(FIST["thumb"])),
                parts=meta)
    with open(os.path.join(OUT, MODEL_ID + ".mesh.json"), "w") as f:
        json.dump(info, f, indent=1)
    return info, buffers


# ----------------------------------------------------------------------------- Blender scene
def to_blender(p):
    """Unity (left-handed, Y up) to Blender (right-handed, Z up) without mirroring: swap Y and Z.
    That reverses handedness, so the triangle winding is reversed too (see assemble)."""
    p = np.asarray(p)
    return np.stack([p[..., 0], p[..., 2], p[..., 1]], axis=-1)


def make_material(image_path):
    image = bpy.data.images.load(image_path)
    mat = bpy.data.materials.new("glove")
    try:
        mat.use_nodes = True
    except Exception:
        pass
    tree = mat.node_tree
    bsdf = tree.nodes.get("Principled BSDF")
    tex = tree.nodes.new("ShaderNodeTexImage")
    tex.image = image
    if bsdf is not None:
        bsdf.inputs["Roughness"].default_value = 0.78
        tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    return mat


def assemble(parts, buffers, curls, name, offset, material, collection, mirror=False):
    worlds = world_transforms(parts, curls)
    for p in parts:
        v, n, uv, t = buffers[p.name]
        R, T = worlds[p.name]
        wv = v @ R.T + T
        wn = n @ R.T
        tt = t[:, ::-1]
        if mirror:
            wv = wv * np.array([-1, 1, 1])
            wn = wn * np.array([-1, 1, 1])
            tt = t
        bv = to_blender(wv) + np.asarray(offset)
        bn = to_blender(wn)
        mesh = bpy.data.meshes.new(name + "_" + p.name)
        mesh.from_pydata([tuple(x) for x in bv], [], [tuple(int(i) for i in f) for f in tt])
        layer = mesh.uv_layers.new(name="UVMap")
        for loop in mesh.loops:
            layer.data[loop.index].uv = tuple(uv[loop.vertex_index])
        mesh.materials.append(material)
        for poly in mesh.polygons:
            poly.use_smooth = True
        mesh.update()
        try:
            mesh.normals_split_custom_set_from_vertices([tuple(x) for x in bn])
        except Exception:
            pass
        obj = bpy.data.objects.new(name + "_" + p.name, mesh)
        collection.objects.link(obj)


def render(folder, parts, buffers, material):
    os.makedirs(folder, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 24
    try:
        scene.cycles.use_denoising = True
    except Exception:
        pass
    scene.render.resolution_x, scene.render.resolution_y = 800, 600
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
    bg = world.node_tree.nodes.get("Background")
    if bg is not None:
        bg.inputs[0].default_value = (0.09, 0.1, 0.12, 1)
        bg.inputs[1].default_value = 0.7

    def light(name, loc, energy, size):
        data = bpy.data.lights.new(name, "AREA")
        data.energy, data.size = energy, size
        obj = bpy.data.objects.new(name, data)
        scene.collection.objects.link(obj)
        obj.location = loc
        from mathutils import Vector
        obj.rotation_euler = (Vector((0, 0, 0)) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    light("Key", (0.6, -0.8, 0.9), 60, 0.8)
    light("Fill", (-0.8, -0.4, 0.3), 20, 1.0)
    light("Rim", (0.0, 0.9, 0.6), 40, 0.8)

    from mathutils import Vector
    cam_data = bpy.data.cameras.new("Camera")
    cam = bpy.data.objects.new("Camera", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    cam_data.lens = 50

    temp = bpy.data.collections.new("Shots")
    scene.collection.children.link(temp)

    def shot(fname, curls, mirror, cam_loc, target=(0, 0.07, -0.03)):
        for o in list(temp.objects):
            bpy.data.objects.remove(o, do_unlink=True)
        assemble(parts, buffers, curls, "shot", (0, 0, 0), material, temp, mirror)
        cam.location = cam_loc
        cam.rotation_euler = (Vector(target) - Vector(cam_loc)).to_track_quat("-Z", "Y").to_euler()
        scene.render.filepath = os.path.join(folder, fname)
        bpy.ops.render.render(write_still=True)

    # Blender coords: glove fingers along -Y? (Unity +Z -> Blender -Y). Palm faces Unity -Y -> Blender -Z.
    # Blender axes here: fingers along +Y, back of the hand up (+Z), right thumb on -X (as in the game).
    t = (0.0, -0.04, -0.03)
    shot("glove_back_open.png", pose_curls(0.0), False, (0.14, 0.14, 0.33), t)
    shot("glove_palm_open.png", pose_curls(0.0), False, (-0.1, 0.12, -0.38), t)
    shot("glove_side_open.png", pose_curls(0.0), False, (-0.42, 0.02, -0.03), t)
    shot("glove_fist.png", pose_curls(1.0), False, (0.22, 0.3, -0.18), (0.0, -0.05, -0.03))
    shot("glove_left_back_open.png", pose_curls(0.0), True, (-0.14, 0.14, 0.33), t)
    bpy.data.collections.remove(temp)


def main():
    started = time.time()
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    parts = build_parts()
    atlas = paint_atlas()
    rgba = np.ones((SIZE, SIZE, 4), dtype=np.float32)
    rgba[..., :3] = atlas
    image = bpy.data.images.new(MODEL_ID + "_atlas", SIZE, SIZE, alpha=False)
    image.pixels.foreach_set(rgba.ravel())
    atlas_path = os.path.join(OUT, MODEL_ID + "_atlas.png")
    image.filepath_raw = atlas_path
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)

    info, buffers = export(parts)
    material = make_material(atlas_path)
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if "--render" in args:
        render(args[args.index("--render") + 1], parts, buffers, material)
    collection = bpy.data.collections.new("Glove")
    bpy.context.scene.collection.children.link(collection)
    assemble(parts, buffers, pose_curls(0.0), "glove_R", (0, 0, 0), material, collection)
    for img in bpy.data.images:
        if img.filepath and os.path.dirname(bpy.path.abspath(img.filepath)) == OUT:
            img.filepath = "//" + os.path.basename(img.filepath)
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, MODEL_ID + ".blend"), compress=True)
    for p in info["parts"]:
        print("PART %-10s %5d triangles %5d vertices" % (p["name"], p["triangles"], p["vertexCount"]))
    print("GLOVE: %d parts, %d triangles per hand, %d vertices, %.1f s" % (len(parts), info["triangles"], info["vertexTotal"], time.time() - started))


if __name__ == "__main__":
    main()
