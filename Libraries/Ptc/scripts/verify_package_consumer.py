"""External, empty-cache PackageReference consumption. No library runtime Python dependency."""
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[3]
PTC = ROOT / "Libraries" / "Ptc"
VERSION = "0.1.0-preview.4"
IDS = {"AthenaAgent.Ptc.Core": "Ptc.Core", "AthenaAgent.Ptc.JavaScript": "Ptc.JavaScript"}
FEED = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "artifacts" / "ptc" / "feed"
EVIDENCE = ROOT / "artifacts" / "ptc"
EVIDENCE.mkdir(parents=True, exist_ok=True)
base = os.environ.get("PTC_TEMP_ROOT") or ("/tmp/opencode" if Path("/tmp/opencode").is_dir() else None)
WORK = Path(tempfile.mkdtemp(prefix="ptc-package-consumer-", dir=base)).resolve()
assert not WORK.is_relative_to(ROOT), "Consumer must be outside the repository"
PACKAGES = WORK / "fresh-packages"
PACKAGES.mkdir()
assert not list(PACKAGES.iterdir())
PROJECT = WORK / "PackageConsumer.csproj"
for name in ["PackageConsumer.csproj", "Program.cs"]:
    shutil.copyfile(PTC / "samples" / "PackageConsumer" / name, WORK / name)
assert not any(word in PROJECT.read_text() for word in ["ProjectReference", "Compile Include", "HintPath", "<Reference "])
packages = ET.parse(PROJECT).findall(".//PackageReference")
assert {p.attrib["Include"]: p.attrib["Version"] for p in packages} == {key: f"[{VERSION}]" for key in IDS}

checksums = {}
for package_id, assembly in IDS.items():
    archive = FEED / f"{package_id}.{VERSION}.nupkg"
    checksums[archive.name] = hashlib.sha256(archive.read_bytes()).hexdigest()
    with zipfile.ZipFile(archive) as package:
        names = package.namelist()
        assert f"lib/net10.0/{assembly}.dll" in names
        assert f"lib/net10.0/{assembly}.xml" in names
        assert {"README.md", "LICENSE", "ThirdPartyNotices.md"}.issubset(names)
        assert not any("obj/" in p or p.endswith(".cs") or "Athena.UI" in p or "Tests" in p or "config.json" in p for p in names)
        spec = ET.fromstring(package.read(next(p for p in names if p.endswith(".nuspec"))))
        ns = {"n": spec.tag.split("}")[0].lstrip("{")}
        meta = spec.find("n:metadata", ns)
        assert meta.find("n:id", ns).text == package_id
        assert meta.find("n:version", ns).text == VERSION
        assert meta.find("n:license", ns).text == "MIT"
        assert meta.find("n:repository", ns).attrib["url"] == "https://github.com/mehaifeng/AthenaAgent"
        deps = meta.findall("n:dependencies/n:group/n:dependency", ns)
        assert all("Athena.UI" not in item.attrib["id"] for item in deps)
        if assembly == "Ptc.JavaScript":
            assert {item.attrib["id"] for item in deps} == {"AthenaAgent.Ptc.Core", "Jint"}
            assert next(item for item in deps if item.attrib["id"] == "Jint").attrib["version"] == "[4.17.0]"
            assert VERSION in next(item for item in deps if item.attrib["id"] == "AthenaAgent.Ptc.Core").attrib["version"]

config = ET.Element("configuration")
sources = ET.SubElement(config, "packageSources")
ET.SubElement(sources, "clear")
ET.SubElement(sources, "add", key="ptc-local", value=str(FEED))
ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
mapping = ET.SubElement(config, "packageSourceMapping")
local = ET.SubElement(mapping, "packageSource", key="ptc-local")
ET.SubElement(local, "package", pattern="AthenaAgent.Ptc.*")
public = ET.SubElement(mapping, "packageSource", key="nuget.org")
ET.SubElement(public, "package", pattern="*")
CONFIG = WORK / "NuGet.Config"
ET.ElementTree(config).write(CONFIG, encoding="utf-8", xml_declaration=True)
environment = dict(os.environ, NUGET_PACKAGES=str(PACKAGES))
log = []

def run(args):
    log.append("COMMAND " + " ".join(map(str, args)))
    outcome = subprocess.run(list(map(str, args)), cwd=WORK, env=environment, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    log.extend([outcome.stdout, "EXIT " + str(outcome.returncode)])
    print(outcome.stdout, flush=True)
    (EVIDENCE / "package-consumer.log").write_text("\n".join(log), encoding="utf-8")
    if outcome.returncode:
        raise SystemExit(outcome.returncode)
    return outcome.stdout

run(["dotnet", "restore", PROJECT, "--configfile", CONFIG, "--packages", PACKAGES, "--no-http-cache", "--force", "--use-lock-file"])
run(["dotnet", "restore", PROJECT, "--configfile", CONFIG, "--packages", PACKAGES, "--locked-mode"])
assets = json.loads((WORK / "obj" / "project.assets.json").read_text())
assert {Path(folder).resolve() for folder in assets["packageFolders"]} == {PACKAGES}
assert not any(item["type"] != "package" for item in assets["libraries"].values())
assert set(assets["libraries"]) == {f"{key}/{VERSION}" for key in IDS} | {"Jint/4.17.0", "Acornima/1.7.0"}
for package_id in IDS:
    archive = FEED / f"{package_id}.{VERSION}.nupkg"
    cache = PACKAGES / package_id.lower() / VERSION
    assert cache.is_dir()
    assert (cache / f"{package_id.lower()}.{VERSION}.nupkg.sha512").read_text().strip() == base64.b64encode(hashlib.sha512(archive.read_bytes()).digest()).decode()

for attempt in [1, 2]:
    output = run(["dotnet", "run", "--project", PROJECT, "-c", "Release", "--no-restore"])
    assert "[PACKAGE CONSUMER PASSED]" in output
    loaded = [json.loads(line[len("ASSEMBLY "):]) for line in output.splitlines() if line.startswith("ASSEMBLY ")]
    assert {item["name"] for item in loaded} == set(IDS.values())
    for item in loaded:
        assert Path(item["path"]).is_relative_to(WORK)
        package_id = next(key for key, value in IDS.items() if value == item["name"])
        dll = PACKAGES / package_id.lower() / VERSION / "lib" / "net10.0" / (item["name"] + ".dll")
        assert item["sha256"] == hashlib.sha256(dll.read_bytes()).hexdigest()

(EVIDENCE / "SHA256SUMS").write_text("".join(f"{value}  feed/{key}\n" for key, value in checksums.items()), encoding="utf-8")
report = {"workDirectory": str(WORK), "freshPackages": str(PACKAGES), "packageIds": list(IDS), "version": VERSION,
          "runs": 2, "assertionsPerRun": 8, "checksums": checksums, "assets": list(assets["libraries"]), "exitCode": 0}
(EVIDENCE / "package-consumer.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("[C18 PASSED] External consumer, fresh cache, exact package/dependency/DLL checks, two successful runs.")
print("Evidence:", EVIDENCE)
