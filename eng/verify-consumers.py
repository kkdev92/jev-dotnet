"""Build isolated consumers from this run's packages and execute the full native smoke app."""
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

repo = Path(__file__).resolve().parents[1]
project_mode = sys.argv[1] == "--projects"
feed = None if project_mode else Path(sys.argv[1]).resolve(strict=True)
core = "Kkdev92.Jev"
di = core + ".DependencyInjection"
ai = core + ".Extensions.AI"
evaluation = ai + ".Evaluation"
packages = {}
for file in ([] if project_mode else feed.glob("*.nupkg")):
    with zipfile.ZipFile(file) as archive:
        manifest, = (s for s in archive.namelist() if s.endswith(".nuspec"))
        metadata = ET.fromstring(archive.read(manifest)).find("{*}metadata")
        package = metadata.findtext("{*}id")
        if package in packages:
            raise ValueError(f"Multiple versions of {package} in {feed}")
        packages[package] = (metadata.findtext("{*}version"), file)
if not project_mode and (set(packages) != {core, di, ai, evaluation} or len({v for v, _ in packages.values()}) != 1):
    raise ValueError("Expected four Jev packages with the same version")
version = None if project_mode else packages[core][0]

with tempfile.TemporaryDirectory(prefix="jev-consumers-") as temp:
    root = Path(temp)
    shutil.copy(repo / "global.json", root)
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    # The mapping holds the Jev packages to this run's feed, so only packages need it. A restore
    # through project references also restores the repository's projects, whose configuration
    # names nuget.org differently, and NuGet keeps one source per URL under the first name it
    # meets: a mapping here can leave nuget.org out.
    mapping = None if project_mode else ET.SubElement(config, "packageSourceMapping")
    for key, url, pattern in [("local", str(feed), core + "*"),
                              ("public", "https://api.nuget.org/v3/index.json", "*")]:
        if key == "local" and project_mode:
            continue
        ET.SubElement(sources, "add", key=key, value=url)
        if mapping is not None:
            source = ET.SubElement(mapping, "packageSource", key=key)
            ET.SubElement(source, "package", pattern=pattern)
    ET.ElementTree(config).write(root / "NuGet.Config", encoding="utf-8", xml_declaration=True)
    sdk = subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip()
    if sdk != json.loads((root / "global.json").read_text())["sdk"]["version"]:
        raise RuntimeError(f"Unexpected SDK {sdk}")
    env = os.environ.copy()
    if not project_mode:
        env["NUGET_PACKAGES"] = str(root / "cache")
    for name, ids in [("core", [core]), ("di", [core, di]), ("ai", [core, ai]),
                      ("integrated", [core, di, ai]), ("full", [core, di, ai, evaluation])]:
        app = root / name
        app.mkdir()
        project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
        props = ET.SubElement(project, "PropertyGroup")
        for key, value in dict(OutputType="Exe", TargetFramework="net10.0", Nullable="enable",
                               ImplicitUsings="enable", TreatWarningsAsErrors="true", NoWarn="JEV001",
                               JsonSerializerIsReflectionEnabledByDefault="false").items():
            ET.SubElement(props, key).text = value
        items = ET.SubElement(project, "ItemGroup")
        for package in ids:
            if project_mode:
                ET.SubElement(items, "ProjectReference", Include=str(repo / "src" / package / (package + ".csproj")))
            else:
                ET.SubElement(items, "PackageReference", Include=package, Version=version)
        ET.ElementTree(project).write(app / "Consumer.csproj", encoding="utf-8")
        tests = repo / "src" / "tests"
        for file in (tests / "Kkdev92.Jev.TestSupport").glob("*.cs"):
            shutil.copy(file, app)
        if name in ("di", "full"):
            directory = "Kkdev92.Jev.AotSmokeTests" if name == "di" else "Kkdev92.Jev.AiAotSmokeTests"
            for file in (tests / directory).glob("*.cs"):
                shutil.copy(file, app)
        elif ai in ids:
            shutil.copy(tests / "Kkdev92.Jev.AiAotSmokeTests" / "TicketGuardScenario.cs", app)
            source = ["using Kkdev92.Jev.AiAotSmokeTests;"]
            if di in ids:
                source += ["using Kkdev92.Jev;", "using Kkdev92.Jev.DependencyInjection;", "using Microsoft.Extensions.DependencyInjection;",
                           "var services = new ServiceCollection();", "services.AddJev(o => o.AuthenticationMode = JevAuthenticationMode.None);",
                           "using var host = services.BuildServiceProvider();", "_ = host.GetRequiredService<JevClient>();"]
            (app / "Program.cs").write_text("\n".join(source + ["return await TicketGuardScenario.RunAsync() ? 0 : 1;"]))
        else:
            (app / "Program.cs").write_text('''using Kkdev92.Jev;
using Kkdev92.Jev.TestSupport;
var b = new JevDecisionPlanBuilder();
var legacy = b.AddNoul("urgent", "Respond today?");
var score = b.AddScore<int>("Rate urgency", [new(10, "low"), new(90, "high")]);
var plan = b.Build();
var json = new SystemOneResponseBuilder().Noul("urgent", 0.9)
    .Score("q0", 0.75, 0.5, ["low", "high"], 0.25, 0.75).Build();
using var http = new HttpClient(FakeHttpMessageHandler.Always(json)) { Timeout = Timeout.InfiniteTimeSpan };
var client = new JevClient(http, new JevClientOptions { AuthenticationMode = JevAuthenticationMode.None });
var batch = await client.EvaluateManyAsync(["ticket"], plan);
var result = batch.Items.Single().Result;
return result?.Get(legacy).Probability == 0.9 && result.GetScore(score).Value == 0.75 ? 0 : 1;
''')
        def run(*args):
            subprocess.run(args, cwd=app, env=env, check=True)
        if name in ("di", "full"):
            run("dotnet", "publish", "-c", "Release", "-p:PublishAot=true", "-o", "native")
            run(str(app / "native" / ("Consumer.exe" if os.name == "nt" else "Consumer")))
        else:
            run("dotnet", "run", "-c", "Release")
        libraries = json.loads((app / "obj/project.assets.json").read_text())["libraries"]
        resolved = {p.partition("/")[0] for p in libraries}
        if {p for p in resolved if p.startswith(core)} != set(ids):
            raise RuntimeError(f"Unexpected Jev dependency graph for {name}")
        if ai not in ids and any(p.startswith("Microsoft.Extensions.AI") for p in resolved):
            raise RuntimeError(f"Unexpected AI dependency for {name}")
        if any(p in resolved for p in ("Polly", "Microsoft.Extensions.AI.Evaluation.Reporting")):
            raise RuntimeError(f"Unexpected optional dependency for {name}")
        if project_mode:
            print(f"Verified {name}, SDK {sdk}, project references and dependency closure", flush=True)
            continue
        for package in ids:
            installed = root / "cache" / libraries[f"{package}/{version}"]["path"]
            marker, = installed.glob("*.nupkg.sha512")
            digest = base64.b64encode(hashlib.sha512(packages[package][1].read_bytes()).digest()).decode()
            if marker.read_text().strip() != digest:
                raise RuntimeError(f"Restored bytes differ for {package}")
        print(f"Verified {name}, SDK {sdk}, local bytes and dependency closure", flush=True)
