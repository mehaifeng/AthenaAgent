"""Prove a real authorization regression is caught, without changing the working tree."""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
SOURCE = ROOT / "Libraries" / "Ptc"
OUT = ROOT / "artifacts" / "ptc"
OUT.mkdir(parents=True, exist_ok=True)
base = "/tmp/opencode" if Path("/tmp/opencode").is_dir() else None
WORK = Path(tempfile.mkdtemp(prefix="ptc-negative-", dir=base)) / "Ptc"
shutil.copytree(SOURCE, WORK, ignore=shutil.ignore_patterns("bin", "obj", ".git"))
target = WORK / "src" / "Ptc.Core" / "PtcExecutor.cs"
original = target.read_text(encoding="utf-8")
needle = "_definitions.TryGetValue(name, out var definition)"
assert original.count(needle) == 1
# Intentional bug: permit case-insensitive lookup of an ordinal capability.
target.write_text(original.replace(needle, "_definitions.TryGetValue(name.ToLowerInvariant(), out var definition)"), encoding="utf-8")
config = WORK / "NuGet.Config"
config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>', encoding="utf-8")
project = WORK / "tests" / "Ptc.Core.Tests" / "Ptc.Core.Tests.csproj"
restore = subprocess.run(["dotnet", "restore", str(project), "--locked-mode", "--configfile", str(config)], cwd=WORK, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
assert restore.returncode == 0, restore.stdout
results = OUT / "negative-results"
command = ["dotnet", "test", str(project), "-c", "Release", "--no-restore", "--filter", "FullyQualifiedName~UnauthorizedAttemptsConsumeBudgetAndStopCannotBeCaught", "--logger", "trx;LogFileName=ordinal-grant-mutation.trx", "--results-directory", str(results)]
run = subprocess.run(command, cwd=WORK, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
(OUT / "authorization-mutation.log").write_text("COPY " + str(WORK) + "\nCOMMAND " + " ".join(command) + "\n" + run.stdout + "\nEXIT " + str(run.returncode), encoding="utf-8")
assert run.returncode != 0, "Authorization regression was not detected"
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
trx = ET.parse(results / "ordinal-grant-mutation.trx")
counters = trx.find(".//t:Counters", ns).attrib
assert counters["executed"] == "1" and counters["failed"] == "1", counters
failure = trx.find(".//t:ErrorInfo/t:Message", ns).text
assert "Assert.Empty" in failure, failure
assert target.read_text(encoding="utf-8") != (SOURCE / "src" / "Ptc.Core" / "PtcExecutor.cs").read_text(encoding="utf-8")
print("[NEGATIVE EVIDENCE PASSED] Ordinal authorization regression: 1 executed assertion fails, exit", run.returncode)
print("Canonical source was never modified. Synthetic host only; evidence:", OUT / "authorization-mutation.log")
