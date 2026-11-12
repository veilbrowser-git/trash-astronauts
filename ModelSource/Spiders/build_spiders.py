"""
Builds the four spider creatures in Blender and writes them out for the Unity project.

Run headless from the project root:
    /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup -P ModelSource/Spiders/build_spiders.py

For each species it writes, next to this file:
    <id>.blend        the model, to open and edit in Blender
    <id>.mesh.json    part list (name, parent, pivot, ranges into the .bin)
    <id>.mesh.bin     vertex data in Unity's coordinate system (float32 pos, normal, uv; uint32 indices)
    <id>_atlas.png    the colour texture every part of that spider uses

The Unity generator (Builder.Spiders.cs) turns those into prefabs. Each spider is anatomically a real
one: a cephalothorax and abdomen joined by a pedicel, eight legs of seven segments, chelicerae with
fangs, pedipalps, eight eyes and spinnerets, covered in thousands of individual hairs (real geometry,
so it works with an ordinary lit shader on a standalone headset).

Blender axes are used throughout: X right, -Y forward, Z up. Triangles are wound so that
cross(b - a, c - a) points outward. Export maps (x, y, z) -> (x, z, -y).
"""
import bpy, json, math, os, random, struct, sys
import numpy as np
from mathutils import Vector, noise

OUT = os.path.dirname(os.path.abspath(__file__))

# ----------------------------------------------------------------------------- atlas layout (u0, u1, v0, v1)
SKIN = (0.02, 0.48, 0.02, 0.98)
HAIR_V = (0.53, 0.97)
HAIR_COLS = [(0.53 + i * 0.115, 0.53 + i * 0.115 + 0.1) for i in range(4)]   # 0 main, 1 accent, 2 pale, 3 dark
SWATCH_V = (0.1, 0.4)
SWATCH = {"fang": (0.53, 0.61), "joint": (0.65, 0.73), "mark": (0.77, 0.85), "claw": (0.89, 0.97)}


def swatch_uv(name):
    u0, u1 = SWATCH[name]
    return ((u0 + u1) * 0.5, (SWATCH_V[0] + SWATCH_V[1]) * 0.5)


class Part:
    """One rigid piece of the spider (a leg section, the abdomen...). Vertices are kept in model space."""

    low = False   # low-poly mode, for the tiny spiderlings: few sides, no rounded ends, no hair

    def __init__(self, name, parent, pivot, material="atlas"):
        self.name, self.parent, self.pivot, self.material = name, parent, Vector(pivot), material
        self.v, self.n, self.uv, self.t = [], [], [], []
        self.skin_tris = []          # (a, b, c, lean Vector or None, hair column) : surface that grows hair

    # -- raw chunks -----------------------------------------------------------------------------
    def add_chunk(self, verts, uvs, tris, lean=None, col=0, hairy=True):
        """Adds a closed-ish surface. Fixes the winding to face outward and computes smooth normals."""
        centre = Vector((0, 0, 0))
        for p in verts:
            centre += p
        centre /= max(1, len(verts))
        facing = 0.0
        for a, b, c in tris:
            nrm = (verts[b] - verts[a]).cross(verts[c] - verts[a])
            facing += nrm.dot((verts[a] + verts[b] + verts[c]) / 3.0 - centre)
        if facing < 0:
            tris = [(a, c, b) for a, b, c in tris]

        acc = {}
        def key(p):
            return (round(p.x, 5), round(p.y, 5), round(p.z, 5))
        for a, b, c in tris:
            nrm = (verts[b] - verts[a]).cross(verts[c] - verts[a])
            for i in (a, b, c):
                k = key(verts[i])
                acc[k] = acc.get(k, Vector((0, 0, 0))) + nrm
        base = len(self.v)
        for p, uv in zip(verts, uvs):
            nrm = acc.get(key(p), Vector((0, 0, 1)))
            self.v.append(p.copy())
            self.n.append(nrm.normalized() if nrm.length > 1e-12 else Vector((0, 0, 1)))
            self.uv.append(uv)
        for a, b, c in tris:
            self.t.append((base + a, base + b, base + c))
            if hairy:
                self.skin_tris.append((base + a, base + b, base + c, lean, col))

    # -- primitives -----------------------------------------------------------------------------
    def tube(self, p0, p1, r0, r1, radial=10, rings=4, bulge=0.12, rect=SKIN, lean=True, col=0, hairy=True, uv_point=None):
        """A tapered, slightly swollen limb segment from p0 to p1 with rounded ends."""
        p0, p1 = Vector(p0), Vector(p1)
        axis = p1 - p0
        length = axis.length
        a = axis / length
        ref = Vector((0, 0, 1)) if abs(a.z) < 0.9 else Vector((1, 0, 0))
        u = a.cross(ref).normalized()
        w = a.cross(u).normalized()
        if Part.low:
            radial = 5
            profile = [(0.0, r0), (length, max(r1, 0.0008))]
        else:
            profile = [(-0.85 * r0, 0.02 * r0), (-0.6 * r0, 0.62 * r0), (-0.25 * r0, 0.93 * r0)]
            for i in range(rings + 1):
                t = i / rings
                profile.append((length * t, (r0 + (r1 - r0) * t) * (1.0 + bulge * math.sin(math.pi * t))))
            profile += [(length + 0.25 * r1, 0.93 * r1), (length + 0.6 * r1, 0.62 * r1), (length + 0.85 * r1, 0.02 * r1)]
        span = profile[-1][0] - profile[0][0]
        verts, uvs, tris = [], [], []
        for i, (d, r) in enumerate(profile):
            centre = p0 + a * d
            for j in range(radial + 1):
                ang = j / radial * math.tau
                verts.append(centre + (u * math.cos(ang) + w * math.sin(ang)) * r)
                if uv_point is not None:
                    uvs.append(uv_point)
                else:
                    uvs.append((rect[0] + (rect[1] - rect[0]) * (j / radial), rect[2] + (rect[3] - rect[2]) * ((d - profile[0][0]) / span)))
        for i in range(len(profile) - 1):
            for j in range(radial):
                q = i * (radial + 1) + j
                tris.append((q, q + 1, q + radial + 1))
                tris.append((q + 1, q + radial + 2, q + radial + 1))
        self.add_chunk(verts, uvs, tris, lean=(a if lean else None), col=col, hairy=hairy)

    def ellipsoid(self, centre, radii, lump=0.0, seed=0.0, seg=26, ring=16, rect=SKIN, shape=None, mark=None, lean=None, col=0, hairy=True, uv_point=None):
        """A lumpy rounded mass. `shape(n)` can reshape it; `mark(p)` says which faces carry the marking colour."""
        centre, radii = Vector(centre), Vector(radii)
        if Part.low:
            seg, ring = min(seg, 8), min(ring, 5)
        verts, uvs, tris = [], [], []
        for i in range(ring + 1):
            v = i / ring
            phi = v * math.pi
            for j in range(seg + 1):
                ang = j / seg * math.tau
                nrm = Vector((math.sin(phi) * math.cos(ang), math.sin(phi) * math.sin(ang), math.cos(phi)))
                p = Vector((nrm.x * radii.x, nrm.y * radii.y, nrm.z * radii.z))
                if shape is not None:
                    p = shape(nrm, p)
                if lump > 0:
                    p *= 1.0 + lump * noise.noise(nrm * 2.3 + Vector((seed, seed * 0.7, seed * 1.3)))
                verts.append(centre + p)
                uvs.append(uv_point if uv_point is not None else (rect[0] + (rect[1] - rect[0]) * (j / seg), rect[2] + (rect[3] - rect[2]) * v))
        for i in range(ring):
            for j in range(seg):
                q = i * (seg + 1) + j
                tris.append((q, q + 1, q + seg + 1))
                tris.append((q + 1, q + seg + 2, q + seg + 1))
        if mark is not None:
            # Faces inside the marking get their own vertices, pointed at the marking swatch.
            muv = swatch_uv("mark")
            plain, marked = [], []
            for tri in tris:
                mid = (verts[tri[0]] + verts[tri[1]] + verts[tri[2]]) / 3.0
                (marked if mark(mid - centre) else plain).append(tri)
            remap = {}
            for tri in marked:
                new = []
                for i in tri:
                    if i not in remap:
                        remap[i] = len(verts)
                        verts.append(verts[i].copy())
                        uvs.append(muv)
                    new.append(remap[i])
                plain.append(tuple(new))
            tris = plain
        self.add_chunk(verts, uvs, tris, lean=lean, col=col, hairy=hairy)

    # -- fur ------------------------------------------------------------------------------------
    def grow_hair(self, rng, density, length, width, lean_amount=0.6, col_fn=None, long_fraction=0.06):
        """Grows individual hairs (thin three-sided spikes) over every hairy surface of this part."""
        if density <= 0 or not self.skin_tris:
            return 0
        areas = np.array([((self.v[b] - self.v[a]).cross(self.v[c] - self.v[a])).length * 0.5 for a, b, c, _, _ in self.skin_tris])
        total = float(areas.sum())
        count = int(total * density)
        if count <= 0 or total <= 0:
            return 0
        picks = np.random.default_rng(rng.randrange(1 << 30)).choice(len(self.skin_tris), size=count, p=areas / total)
        for pick in picks:
            a, b, c, lean, col = self.skin_tris[int(pick)]
            r1, r2 = rng.random(), rng.random()
            if r1 + r2 > 1.0:
                r1, r2 = 1.0 - r1, 1.0 - r2
            r0 = 1.0 - r1 - r2
            root = self.v[a] * r0 + self.v[b] * r1 + self.v[c] * r2
            nrm = (self.n[a] * r0 + self.n[b] * r1 + self.n[c] * r2).normalized()
            jitter = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))) * 0.3
            lean_vec = lean if lean is not None else Vector((0, 0.7, -0.4))   # body hair sweeps back and down
            direction = (nrm * (1.0 - lean_amount) + lean_vec * lean_amount + jitter).normalized()
            if direction.dot(nrm) < 0.12:                                     # never lie flat or grow inward
                direction = (direction + nrm * 0.5).normalized()
            hair_len = rng.uniform(length[0], length[1])
            if rng.random() < long_fraction:
                hair_len *= rng.uniform(1.7, 2.6)                             # the odd long guard hair
            side = direction.cross(nrm)
            side = side.normalized() if side.length > 1e-6 else direction.cross(Vector((1, 0, 0))).normalized()
            up = direction.cross(side).normalized()
            w = width * rng.uniform(0.7, 1.2)
            base = len(self.v)
            column = col_fn(root, col) if col_fn is not None else col
            u0, u1 = HAIR_COLS[column]
            hu = rng.uniform(u0, u1)
            shade = (nrm * 0.75 + direction * 0.25).normalized()               # shade like the skin underneath: soft, not noisy
            corners = []
            for k in range(3):
                ang = k * math.tau / 3.0
                corners.append(root - nrm * 0.002 + (side * math.cos(ang) + up * math.sin(ang)) * (w * 0.5))
            bend = (lean_vec - nrm * 0.3) * (hair_len * 0.18)
            tip = root + direction * hair_len + bend
            for p in corners:
                self.v.append(p); self.n.append(shade); self.uv.append((hu, HAIR_V[0]))
            self.v.append(tip); self.n.append(shade); self.uv.append((hu, HAIR_V[1]))
            for k in range(3):
                i0, i1, i2 = base + k, base + (k + 1) % 3, base + 3
                face = (self.v[i1] - self.v[i0]).cross(self.v[i2] - self.v[i0])
                outward = (self.v[i0] + self.v[i1]) * 0.5 - root
                self.t.append((i0, i1, i2) if face.dot(outward) >= 0 else (i0, i2, i1))
        return count


# ----------------------------------------------------------------------------- species
SPECIES = {
    # The young: the tarantula's shape with almost no detail, because there are dozens of them and each is thumb-sized.
    "spiderling": dict(low=True, body=(0.13, 0.17, 0.09), abdomen=(0.15, 0.2, 0.14), leg_len=1.0, leg_r=1.3,
                       hair=0, hair_len=(0.02, 0.04), hair_w=0.005, eye=(0.02, 0.015, 0.013, 0.014),
                       skin=(0.2, 0.15, 0.11), hair_root=(0.1, 0.07, 0.05), hair_tip=(0.4, 0.3, 0.2),
                       accent=(0.7, 0.3, 0.1), pale=(0.7, 0.6, 0.5), mark=(0.3, 0.2, 0.14), smoothness=0.4, marking="none"),
    # A giant tarantula: heavy, dark, densely furred, rust-orange hair on the knees.
    "janitor": dict(body=(0.13, 0.17, 0.09), abdomen=(0.16, 0.22, 0.15), leg_len=1.0, leg_r=1.25,
                    hair=5200, hair_len=(0.022, 0.055), hair_w=0.0055, eye=(0.021, 0.015, 0.013, 0.014),
                    skin=(0.075, 0.058, 0.05), hair_root=(0.07, 0.05, 0.045), hair_tip=(0.42, 0.33, 0.27),
                    accent=(0.86, 0.36, 0.08), pale=(0.72, 0.62, 0.5), mark=(0.3, 0.2, 0.14), smoothness=0.22, marking="stripe"),
    # A blind cave hunter: pale, flat, enormously long thin legs with sparse long sensory hairs.
    "listener": dict(body=(0.1, 0.14, 0.06), abdomen=(0.1, 0.2, 0.09), leg_len=1.55, leg_r=0.62,
                     hair=1700, hair_len=(0.035, 0.1), hair_w=0.0035, eye=(0.007, 0.006, 0.006, 0.006),
                     skin=(0.6, 0.52, 0.42), hair_root=(0.5, 0.42, 0.33), hair_tip=(0.92, 0.88, 0.8),
                     accent=(0.8, 0.7, 0.55), pale=(0.95, 0.93, 0.88), mark=(0.42, 0.33, 0.25), smoothness=0.42, marking="none"),
    # A stocky hunter with two huge forward eyes, grey-brown with banded legs.
    "warden": dict(body=(0.17, 0.2, 0.13), abdomen=(0.17, 0.22, 0.16), leg_len=0.86, leg_r=1.5,
                   hair=5200, hair_len=(0.018, 0.042), hair_w=0.0055, eye=(0.036, 0.022, 0.016, 0.018),
                   skin=(0.2, 0.17, 0.13), hair_root=(0.2, 0.17, 0.13), hair_tip=(0.7, 0.64, 0.54),
                   accent=(0.06, 0.05, 0.045), pale=(0.85, 0.8, 0.7), mark=(0.62, 0.55, 0.42), smoothness=0.24, marking="chevron"),
    # A widow: glossy and almost hairless, a swollen abdomen with a violet hourglass, long fine legs.
    "mimic": dict(body=(0.095, 0.13, 0.075), abdomen=(0.2, 0.22, 0.2), leg_len=1.3, leg_r=0.7,
                  hair=650, hair_len=(0.012, 0.03), hair_w=0.003, eye=(0.014, 0.011, 0.01, 0.01),
                  skin=(0.03, 0.018, 0.045), hair_root=(0.03, 0.02, 0.04), hair_tip=(0.2, 0.12, 0.26),
                  accent=(0.18, 0.08, 0.24), pale=(0.5, 0.35, 0.6), mark=(0.86, 0.18, 0.72), smoothness=0.8, marking="hourglass"),
}

LEG_SEGMENTS = [  # name, length, elevation in degrees, radius at start, radius at end
    ("coxa", 0.045, 8, 0.03, 0.028), ("trochanter", 0.032, 24, 0.027, 0.026), ("femur", 0.27, 40, 0.027, 0.022),
    ("patella", 0.07, 6, 0.024, 0.021), ("tibia", 0.23, -34, 0.021, 0.016), ("metatarsus", 0.21, -60, 0.015, 0.01),
    ("tarsus", 0.09, -72, 0.0095, 0.0055),
]
LEG_AZIMUTH = [56, 20, -16, -52]         # degrees toward the front, for the four leg rows
LEG_ROW_LENGTH = [1.1, 0.95, 0.9, 1.15]  # first and last pairs are the longest, as on a real spider


def build(species_id, spec):
    Part.low = spec.get("low", False)
    low = Part.low
    rng = random.Random({"janitor": 11, "listener": 23, "warden": 37, "mimic": 53}.get(species_id, 71))
    bx, by, bz = spec["body"]
    ax, ay, az = spec["abdomen"]
    parts = []

    def part(name, parent, pivot, material="atlas"):
        p = Part(name, parent, pivot, material)
        parts.append(p)
        return p

    body = part("Body", None, (0, 0, 0))                     # empty root that everything hangs from

    # --- cephalothorax: a shield, narrower and higher at the head end
    head = part("Head", "Body", (0, 0, 0))
    def carapace(n, p):
        front = max(0.0, -n.y)
        p.x *= 1.0 - 0.26 * front
        p.z *= 1.0 + 0.22 * front if n.z > 0 else 0.7
        return p
    head.ellipsoid((0, 0, 0), (bx, by, bz), lump=0.035, seed=1.7, shape=carapace, lean=Vector((0, 0.8, -0.3)))
    # sternum / mouth parts underneath, and the pedicel to the abdomen
    if not low:
        head.ellipsoid((0, -by * 0.1, -bz * 0.55), (bx * 0.55, by * 0.6, bz * 0.3), lump=0.02, seed=4.2, seg=14, ring=8, hairy=False, uv_point=swatch_uv("joint"))
    head.tube((0, by * 0.8, -0.005), (0, by * 1.02, 0.0), bx * 0.26, bx * 0.22, radial=8, rings=2, hairy=False, uv_point=swatch_uv("joint"))

    # --- abdomen: an egg, fattest toward the back
    abdomen = part("Abdomen", "Body", (0, by * 0.95, 0.0))
    centre = Vector((0, by * 0.95 + ay * 0.92, az * 0.22))
    def egg(n, p):
        k = 0.86 + 0.16 * n.y
        p.x *= k
        p.z *= k
        return p
    marking = spec["marking"]
    def mark(d):
        if d.z < az * 0.25:
            return False
        if marking == "hourglass":                             # two triangles meeting at a waist
            y = d.y / ay
            return abs(d.x) < ax * (0.07 + 0.42 * abs(y + 0.05)) and abs(y + 0.05) < 0.5
        if marking == "stripe":                                # a pale band down the middle
            return abs(d.x) < ax * 0.1 and d.y < ay * 0.7
        if marking == "chevron":                               # rows of V shapes
            y = (d.y / ay + 1.0) * 3.0
            return abs((y % 1.0) - 0.5 - abs(d.x) / ax * 0.9) < 0.11 and abs(d.x) < ax * 0.7
        return False
    abdomen.ellipsoid(centre, (ax, ay, az), lump=0.05, seed=2.9, seg=30, ring=18, shape=egg, mark=mark if marking != "none" else None,
                      lean=Vector((0, 0.85, -0.25)))
    for side in (() if low else (-1, 1)):                       # spinnerets
        tip = centre + Vector((side * 0.022, ay * 0.98, -az * 0.3))
        abdomen.tube(tip, tip + Vector((side * 0.006, 0.045, -0.02)), 0.014, 0.004, radial=6, rings=2, hairy=False, uv_point=swatch_uv("joint"))

    # --- eight eyes in two rows on the front of the carapace
    def on_carapace(x, y, z, out=0.0):
        d = Vector((x / bx, y / by, z / bz)).normalized()
        p = carapace(d, Vector((d.x * bx, d.y * by, d.z * bz)))
        return p + d * out
    e_ame, e_ale, e_pme, e_ple = spec["eye"]
    front = max(0.24, e_ame * 1.25 / bx)
    eye_spots = [(front, -0.9, 0.62, e_ame), (max(0.58, front + (e_ame + e_ale) * 1.15 / bx), -0.78, 0.5, e_ale), (0.3, -0.66, 0.92, e_pme), (0.66, -0.5, 0.78, e_ple)]
    index = 0
    for fx, fy, fz, size in (eye_spots[:1] if low else eye_spots):
        for side in (-1, 1):
            p = on_carapace(side * fx * bx, fy * by, fz * bz, size * 0.25)
            eye = part("Eye_%d" % index, "Head", p, material="eye")
            eye.ellipsoid(p, (size, size, size), seg=12, ring=8, hairy=False)
            index += 1

    # --- chelicerae and fangs (one part, hinged at the face: the "jaw")
    jaw_pivot = Vector((0, -by * 0.84, -bz * 0.1))
    jaw = part("Jaw", "Head", jaw_pivot)
    ch = 0.036 * (bx / 0.13)
    for side in (-1, 1):
        base = jaw_pivot + Vector((side * bx * 0.3, 0, 0))
        knuckle = base + Vector((side * 0.004, -by * 0.2, -bz * 0.75))
        jaw.tube(base, knuckle, ch, ch * 0.82, radial=10, rings=3, bulge=0.2, col=3)
        f1 = knuckle + Vector((-side * 0.006, -0.02, -0.05))
        f2 = f1 + Vector((-side * 0.016, 0.014, -0.04))
        f3 = f2 + Vector((-side * 0.012, 0.022, -0.022))
        jaw.tube(knuckle, f1, ch * 0.5, ch * 0.38, radial=8, rings=2, bulge=0.0, hairy=False, uv_point=swatch_uv("fang"))
        jaw.tube(f1, f2, ch * 0.38, ch * 0.2, radial=8, rings=2, bulge=0.0, hairy=False, uv_point=swatch_uv("fang"))
        jaw.tube(f2, f3, ch * 0.2, 0.0015, radial=8, rings=2, bulge=0.0, hairy=False, uv_point=swatch_uv("fang"))

    # --- pedipalps: two short feelers beside the mouth
    for i, side in enumerate((-1, 1)):
        base = Vector((side * bx * 0.52, -by * 0.72, -bz * 0.2))
        palp = part("Palp_%d" % i, "Head", base)
        p1 = base + Vector((side * 0.03, -0.07, 0.012))
        p2 = p1 + Vector((side * 0.006, -0.075, -0.012))
        p3 = p2 + Vector((-side * 0.004, -0.05, -0.04))
        r = 0.02 * spec["leg_r"] ** 0.5
        palp.tube(base, p1, r, r * 0.9)
        palp.tube(p1, p2, r * 0.88, r * 0.75)
        palp.tube(p2, p3, r * 0.75, r * 0.4, col=1)

    # --- eight legs of seven segments, each in two rigid parts hinged at the knee
    leg = 0
    for row in range(4):
        for side in (-1, 1):
            az_ang = math.radians(LEG_AZIMUTH[row])
            flat = Vector((side * math.cos(az_ang), -math.sin(az_ang), 0))
            attach = Vector((side * bx * 0.8, by * (-0.52 + row * 0.33), -bz * 0.28))
            scale_len = spec["leg_len"] * LEG_ROW_LENGTH[row]
            scale_r = spec["leg_r"]
            upper = part("Leg_%d" % leg, "Body", attach)
            lower = None
            cursor = attach.copy()
            for name, length, elevation, r0, r1 in LEG_SEGMENTS:
                el = math.radians(elevation + rng.uniform(-3, 3))
                direction = (flat * math.cos(el) + Vector((0, 0, 1)) * math.sin(el)).normalized()
                # short segments near the body do not stretch with long-legged species as much
                seg_len = length * (scale_len if name in ("femur", "tibia", "metatarsus", "tarsus") else scale_len ** 0.4)
                end = cursor + direction * seg_len
                if name == "tibia":
                    lower = part("LegLower_%d" % leg, "Leg_%d" % leg, cursor)
                target = lower if lower is not None else upper
                column = 1 if name == "patella" else 0
                target.tube(cursor, end, r0 * scale_r, r1 * scale_r, radial=10, rings=4 if length > 0.1 else 2,
                            bulge=0.16 if name in ("femur", "tibia") else 0.08, col=column)
                # a paler membrane ring at each joint
                if not low:
                    target.ellipsoid(cursor, (r0 * scale_r * 0.94,) * 3, seg=10, ring=6, hairy=False, uv_point=swatch_uv("joint"))
                cursor = end
            for k in (() if low else (-1, 1)):                   # two tarsal claws
                side_vec = flat.cross(Vector((0, 0, 1))) * (0.006 * k)
                lower.tube(cursor, cursor + side_vec + flat * 0.012 + Vector((0, 0, -0.028)), 0.004 * scale_r, 0.0008, radial=6, rings=2, bulge=0.0,
                           hairy=False, uv_point=swatch_uv("claw"))
            leg += 1

    # --- fur
    banded = species_id == "warden"
    def column_for(root, col):
        if col != 0:
            return col
        if banded and int((root.length) / 0.07) % 2 == 0:       # dark and light bands along the legs
            return 1
        r = rng.random()
        return 2 if r < 0.07 else 3 if r < 0.2 else 0
    hairs = 0
    for p in parts:
        if p.material != "atlas":
            continue
        is_leg = p.name.startswith("Leg") or p.name.startswith("Palp")
        density = spec["hair"] * (1.0 if is_leg else 0.85)
        hairs += p.grow_hair(rng, density, spec["hair_len"] if is_leg else (spec["hair_len"][0] * 1.1, spec["hair_len"][1] * 1.25),
                             spec["hair_w"], lean_amount=0.62 if is_leg else 0.5, col_fn=column_for)
    return parts, hairs


# ----------------------------------------------------------------------------- texture
def make_atlas(spec, size=512):
    rng = np.random.default_rng(7)
    img = np.zeros((size, size, 4), dtype=np.float32)
    img[..., 3] = 1.0
    ys, xs = np.mgrid[0:size, 0:size].astype(np.float32) / size   # ys = v (row 0 is the bottom), xs = u

    def fill(u0, u1, v0, v1, rgb):
        img[int(v0 * size):int(v1 * size), int(u0 * size):int(u1 * size), :3] = rgb

    # skin: mottled chitin with fine pores where hairs root
    skin = np.array(spec["skin"], dtype=np.float32)
    blotch = (np.sin(xs * 41.0 + np.sin(ys * 23.0) * 2.0) * np.sin(ys * 37.0 + np.sin(xs * 17.0) * 2.0)) * 0.5 + 0.5
    grain = rng.random((size, size)).astype(np.float32)
    pores = (rng.random((size, size)) > 0.985).astype(np.float32)
    value = 0.78 + blotch * 0.3 + (grain - 0.5) * 0.22 - pores * 0.4
    left = slice(0, size // 2)
    img[:, left, :3] = np.clip(skin[None, None, :] * value[:, left, None] * 1.25, 0, 1)

    # hair: dark at the root, the species colour along the shaft, paler at the tip, each strand a little different
    v0, v1 = 0.5, 1.0
    rows = slice(int(v0 * size), size)
    t = np.clip((ys[rows, :] - HAIR_V[0]) / (HAIR_V[1] - HAIR_V[0]), 0, 1)[..., None]
    root = np.array(spec["hair_root"], dtype=np.float32)
    def gradient(tip):
        tip = np.array(tip, dtype=np.float32)
        mid = root * 0.45 + tip * 0.55
        return np.where(t < 0.45, root + (mid - root) * (t / 0.45), mid + (tip - mid) * ((t - 0.45) / 0.55))
    strand = (0.82 + 0.36 * rng.random((1, size, 1))).astype(np.float32)
    tips = [spec["hair_tip"], spec["accent"], spec["pale"], tuple(c * 0.35 for c in spec["hair_tip"])]
    for (u0, u1), tip in zip(HAIR_COLS, tips):
        cols = slice(int((u0 - 0.012) * size), int((u1 + 0.012) * size))
        img[rows, cols, :3] = np.clip(gradient(tip)[:, cols, :] * strand[:, cols, :], 0, 1)

    # flat swatches
    fill(SWATCH["fang"][0] - 0.02, SWATCH["fang"][1] + 0.02, 0.0, 0.5, (0.06, 0.012, 0.01))
    fill(SWATCH["joint"][0] - 0.02, SWATCH["joint"][1] + 0.02, 0.0, 0.5, tuple(min(1.0, c * 1.9 + 0.05) for c in spec["skin"]))
    fill(SWATCH["mark"][0] - 0.02, SWATCH["mark"][1] + 0.02, 0.0, 0.5, spec["mark"])
    fill(SWATCH["claw"][0] - 0.02, SWATCH["claw"][1] + 0.03, 0.0, 0.5, (0.02, 0.015, 0.012))
    return img


# ----------------------------------------------------------------------------- output
def to_unity(v):
    return (v.x, v.z, -v.y)


def export(species_id, spec, parts, hairs):
    image = bpy.data.images.new(species_id + "_atlas", 512, 512, alpha=False)
    image.pixels.foreach_set(make_atlas(spec).ravel())
    image.filepath_raw = os.path.join(OUT, species_id + "_atlas.png")
    image.file_format = "PNG"
    image.save()

    positions, normals, uvs, indices, meta = [], [], [], [], []
    for p in parts:
        v_start, i_start = len(positions) // 3, len(indices)
        for v, n, uv in zip(p.v, p.n, p.uv):
            positions.extend(to_unity(v - p.pivot))
            normals.extend(to_unity(n))
            uvs.extend(uv)
        for a, b, c in p.t:
            indices.extend((a, b, c))
        meta.append(dict(name=p.name, parent=p.parent or "", pivot=list(to_unity(p.pivot)), material=p.material,
                         vertexStart=v_start, vertexCount=len(p.v), indexStart=i_start, indexCount=len(p.t) * 3))
    with open(os.path.join(OUT, species_id + ".mesh.bin"), "wb") as f:
        f.write(struct.pack("<%df" % len(positions), *positions))
        f.write(struct.pack("<%df" % len(normals), *normals))
        f.write(struct.pack("<%df" % len(uvs), *uvs))
        f.write(struct.pack("<%dI" % len(indices), *indices))
    info = dict(id=species_id, smoothness=spec["smoothness"], vertexTotal=len(positions) // 3, indexTotal=len(indices),
                triangles=len(indices) // 3, hairs=hairs, parts=meta)
    with open(os.path.join(OUT, species_id + ".mesh.json"), "w") as f:
        json.dump(info, f, indent=1)

    # The same model as real Blender objects, saved so it can be opened and edited.
    bpy.ops.wm.read_factory_settings(use_empty=True)
    image = bpy.data.images.load(os.path.join(OUT, species_id + "_atlas.png"))
    material = bpy.data.materials.new(species_id + "_skin")
    material.use_nodes = True
    bsdf = material.node_tree.nodes.get("Principled BSDF")
    tex = material.node_tree.nodes.new("ShaderNodeTexImage")
    tex.image = image
    if bsdf is not None:
        material.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        bsdf.inputs["Roughness"].default_value = 1.0 - spec["smoothness"]
    eye_material = bpy.data.materials.new(species_id + "_eye")
    eye_material.use_nodes = True
    eye_bsdf = eye_material.node_tree.nodes.get("Principled BSDF")
    if eye_bsdf is not None:
        eye_bsdf.inputs["Base Color"].default_value = (0.01, 0.005, 0.005, 1)
        eye_bsdf.inputs["Roughness"].default_value = 0.05
    objects = {}
    for p in parts:
        mesh = bpy.data.meshes.new(p.name)
        mesh.from_pydata([tuple(v - p.pivot) for v in p.v], [], p.t)
        if p.uv:
            layer = mesh.uv_layers.new(name="UVMap")
            for loop in mesh.loops:
                layer.data[loop.index].uv = p.uv[loop.vertex_index]
        mesh.materials.append(eye_material if p.material == "eye" else material)
        for poly in mesh.polygons:
            poly.use_smooth = True
        mesh.update()
        obj = bpy.data.objects.new(p.name, mesh)
        bpy.context.scene.collection.objects.link(obj)
        objects[p.name] = (obj, p)
    for name, (obj, p) in objects.items():
        if p.parent:
            parent_obj, parent_part = objects[p.parent]
            obj.parent = parent_obj
            obj.location = p.pivot - parent_part.pivot
        else:
            obj.location = p.pivot
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, species_id + ".blend"), compress=True)
    return info


if __name__ == "__main__":
    for species_id, spec in SPECIES.items():
        parts, hairs = build(species_id, spec)
        info = export(species_id, spec, parts, hairs)
        print("SPIDER %s: %d parts, %d hairs, %d triangles, %d vertices" % (species_id, len(parts), hairs, info["triangles"], info["vertexTotal"]))
