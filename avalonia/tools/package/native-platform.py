#!/usr/bin/env python3
"""Prune foreign native payloads before signing; audit the final archive without extraction.

Managed PE assemblies are portable, unlike native PE DLLs. File suffixes alone cannot
make that distinction (nor detect extensionless Bun and .node addons).
"""
import argparse
import contextlib
import pathlib
import struct
import tarfile
import zipfile

RIDS = {f"{os}-{arch}" for os in ("osx", "linux", "win") for arch in ("arm64", "x64")}


def native_target(stream):
    """Return (OS, architectures), or None for data/managed assemblies."""
    header = stream.read(64)
    if header[:4] == b"\x7fELF":
        endian = "<" if header[5] == 1 else ">"
        machine = struct.unpack_from(endian + "H", header, 18)[0]
        return "linux", { {62: "x64", 183: "arm64"}.get(machine, str(machine)) }
    magic = header[:4]
    mach = {b"\xcf\xfa\xed\xfe": "<", b"\xfe\xed\xfa\xcf": ">",
            b"\xce\xfa\xed\xfe": "<", b"\xfe\xed\xfa\xce": ">"}
    cpus = {0x01000007: "x64", 0x0100000c: "arm64"}
    if magic in mach:
        cpu = struct.unpack_from(mach[magic] + "I", header, 4)[0]
        return "osx", {cpus.get(cpu, str(cpu))}
    if magic in (b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"):
        count = struct.unpack_from(">I", header, 4)[0]
        # CAFEBABE is also Java class magic; actual fat binaries have few slices.
        if count > 32:
            return None
        stride = 32 if magic[-1] == 0xbf else 20
        stream.seek(8)
        slices = stream.read(count * stride)
        return "osx", {cpus.get(struct.unpack_from(">I", slices, i * stride)[0], "unknown") for i in range(count)}
    if header[:2] == b"MZ":
        pe = struct.unpack_from("<I", header, 60)[0]
        stream.seek(pe)
        coff = stream.read(24)
        if coff[:4] != b"PE\0\0":
            return None
        machine = struct.unpack_from("<H", coff, 4)[0]
        optional = stream.read(struct.unpack_from("<H", coff, 20)[0])
        kind = struct.unpack_from("<H", optional)[0]
        directories = 112 if kind == 0x20b else 96
        cli = directories + 14 * 8
        if len(optional) >= cli + 8 and struct.unpack_from("<I", optional, cli)[0]:
            return None  # CLR assembly, including portable framework DLLs
        return "win", {{0x8664: "x64", 0xaa64: "arm64"}.get(machine, str(machine))}
    return None


def foreign(stream, rid):
    target = native_target(stream)
    os, arch = rid.split("-")
    return target is not None and (target[0] != os or arch not in target[1])


def audit(path, rid, prune=False):
    rejected = []
    if path.is_dir():
        for file in sorted(path.rglob("*")):
            if not file.is_file() or file.is_symlink():
                continue
            with file.open("rb") as stream:
                wrong = foreign(stream, rid)
            if wrong:
                rejected.append(str(file.relative_to(path)))
                if prune:
                    file.unlink()
    elif zipfile.is_zipfile(path):
        with zipfile.ZipFile(path) as archive:
            for entry in archive.infolist():
                if not entry.is_dir():
                    with archive.open(entry) as stream:
                        if foreign(stream, rid):
                            rejected.append(entry.filename)
    else:
        with tarfile.open(path, "r:*") as archive:
            for entry in archive:
                if entry.isfile():
                    with contextlib.closing(archive.extractfile(entry)) as stream:
                        if foreign(stream, rid):
                            rejected.append(entry.name)
    return rejected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rid", choices=sorted(RIDS))
    parser.add_argument("path", type=pathlib.Path)
    parser.add_argument("--prune", action="store_true")
    args = parser.parse_args()
    if args.prune and not args.path.is_dir():
        parser.error("--prune requires an unsigned staging directory")
    rejected = audit(args.path, args.rid, args.prune)
    for name in rejected:
        print(f"{'removed' if args.prune else 'FOREIGN NATIVE'}: {name}")
    if rejected and not args.prune:
        raise SystemExit(1)
    print(f"native-platform: {args.rid}: {'pruned ' + str(len(rejected)) if args.prune else 'passed'}")


if __name__ == "__main__":
    main()
