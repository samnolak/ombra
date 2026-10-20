#!/usr/bin/env python3
"""Cross-platform package gate tests; no native binaries need to execute."""
import importlib.util
import io
import pathlib
import struct
import tarfile
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("native_platform", pathlib.Path(__file__).with_name("native-platform.py"))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def binary(os, arch, managed=False):
    data = bytearray(512)
    if os == "linux":
        data[:6] = b"\x7fELF\x02\x01"
        struct.pack_into("<H", data, 18, 62 if arch == "x64" else 183)
    elif os == "osx":
        data[:4] = b"\xcf\xfa\xed\xfe"
        struct.pack_into("<I", data, 4, 0x01000007 if arch == "x64" else 0x0100000c)
    else:
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 60, 64)
        data[64:68] = b"PE\0\0"
        struct.pack_into("<H", data, 68, 0x8664 if arch == "x64" else 0xaa64)
        struct.pack_into("<H", data, 84, 240)
        struct.pack_into("<H", data, 88, 0x20b)
        if managed:
            struct.pack_into("<I", data, 88 + 112 + 14 * 8, 4096)
    return bytes(data)


class NativePlatformTests(unittest.TestCase):
    def test_every_rid_rejects_every_foreign_platform_and_cpu(self):
        for rid in sorted(gate.RIDS):
            for candidate in sorted(gate.RIDS):
                with self.subTest(rid=rid, candidate=candidate):
                    self.assertEqual(rid != candidate, gate.foreign(io.BytesIO(binary(*candidate.split("-"))), rid))

    def test_managed_dlls_and_data_are_portable(self):
        for rid in gate.RIDS:
            self.assertFalse(gate.foreign(io.BytesIO(binary("win", "x64", managed=True)), rid))
            self.assertFalse(gate.foreign(io.BytesIO(b"localization text"), rid))

    def test_universal_macos_binary(self):
        data = bytearray(64)
        struct.pack_into(">IIII", data, 0, 0xcafebabe, 2, 0x01000007, 0)
        struct.pack_into(">I", data, 28, 0x0100000c)
        for arch in ("x64", "arm64"):
            self.assertFalse(gate.foreign(io.BytesIO(data), "osx-" + arch))
        self.assertTrue(gate.foreign(io.BytesIO(data), "linux-x64"))

    def test_pruning_and_final_zip_tar_gate(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            stage = root / "stage"
            stage.mkdir()
            (stage / "libSkiaSharp.dylib").write_bytes(binary("osx", "arm64"))
            (stage / "foreign.node").write_bytes(binary("linux", "x64"))
            (stage / "sherpa.dll").write_bytes(binary("win", "arm64"))
            (stage / "Ombra.dll").write_bytes(binary("win", "x64", managed=True))
            self.assertEqual(["foreign.node", "sherpa.dll"], gate.audit(stage, "osx-arm64"))
            for extension in ("zip", "tar.gz"):
                archive = root / ("package." + extension)
                if extension == "zip":
                    with zipfile.ZipFile(archive, "w") as out:
                        for file in stage.iterdir():
                            out.write(file, file.name)
                else:
                    with tarfile.open(archive, "w:gz") as out:
                        for file in stage.iterdir():
                            out.add(file, file.name)
                self.assertEqual({"foreign.node", "sherpa.dll"}, set(gate.audit(archive, "osx-arm64")))
            self.assertEqual(["foreign.node", "sherpa.dll"], gate.audit(stage, "osx-arm64", prune=True))
            self.assertEqual([], gate.audit(stage, "osx-arm64"))
            self.assertTrue((stage / "Ombra.dll").exists())


if __name__ == "__main__":
    unittest.main()
