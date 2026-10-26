"""Minimal reader for a Unity 2019 SerializedFile (.assets): header, types, object table, externals. Read-only."""
import struct, sys

class R:
    def __init__(self, b, pos=0, le=True): self.b, self.p, self.le = b, pos, le
    def _u(self, fmt, n):
        v = struct.unpack_from(("<" if self.le else ">") + fmt, self.b, self.p)[0]; self.p += n; return v
    def u8(self): return self._u("B", 1)
    def i16(self): return self._u("h", 2)
    def i32(self): return self._u("i", 4)
    def u32(self): return self._u("I", 4)
    def i64(self): return self._u("q", 8)
    def f32(self): return self._u("f", 4)
    def f64(self): return self._u("d", 8)
    def cstr(self):
        e = self.b.index(b"\0", self.p); s = self.b[self.p:e].decode("utf-8", "replace"); self.p = e + 1; return s
    def align(self, n=4): self.p = (self.p + n - 1) // n * n
    def astr(self):  # Unity serialized string: int32 length, bytes, align 4
        n = self.i32(); s = self.b[self.p:self.p + n].decode("utf-8", "replace"); self.p += n; self.align(); return s
    def pptr(self): return (self.i32(), self.i64())

def load(path):
    b = open(path, "rb").read()
    h = R(b, 0, le=False)
    meta_size, file_size, version, data_off = h.u32(), h.u32(), h.u32(), h.u32()
    endian = h.u8(); h.p += 3
    if version >= 22:
        meta_size, file_size, data_off = h.u32(), h.i64(), h.i64(); h.i64()
    r = R(b, h.p, le=(endian == 0))
    unity = r.cstr(); platform = r.i32(); typetree = bool(r.u8())
    ntypes = r.i32(); types = []
    for _ in range(ntypes):
        cid = r.i32(); stripped = r.u8(); script_idx = r.i16()
        if cid == 114: r.p += 16
        r.p += 16
        if typetree: raise SystemExit("type trees present; extend parser")
        types.append((cid, script_idx))
    nobj = r.i32(); objs = {}
    for _ in range(nobj):
        r.align(4); pid = r.i64()
        start = r.i64() if version >= 22 else r.u32()
        size = r.u32(); tidx = r.i32()
        objs[pid] = (data_off + start, size, types[tidx][0], types[tidx][1])
    nscript = r.i32(); scripts = []
    for _ in range(nscript):
        fi = r.i32(); r.align(4); scripts.append((fi, r.i64()))
    next_ = r.i32(); externals = []
    for _ in range(next_):
        r.cstr(); r.p += 16; r.i32(); externals.append(r.cstr())
    return dict(bytes=b, version=version, unity=unity, typetree=typetree, objects=objs, scripts=scripts, externals=externals)

if __name__ == "__main__":
    f = load(sys.argv[1])
    from collections import Counter
    print("format", f["version"], "unity", f["unity"], "typetree", f["typetree"], "objects", len(f["objects"]))
    print("class counts", Counter(o[2] for o in f["objects"].values()).most_common(12))
    print("externals", f["externals"][:10])
