"""Verify the local package's contents and dependency license metadata."""
import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET
from zipfile import ZipFile

root = Path(__file__).resolve().parents[1]
packages = list((root / "artifacts/packages").glob("*.nupkg"))
assert len(packages) == 1, "Exactly one runtime NuGet package is required"
with ZipFile(packages[0]) as archive:
    names = set(archive.namelist())
    assemblies = sorted(n for n in names if n.endswith(".dll"))
    assert assemblies == ["lib/net10.0/MockProxyGateway.Core.dll"], assemblies
    assert "lib/net10.0/MockProxyGateway.Core.xml" in names, "Missing public XML docs"
    assert "README.md" in names and "LICENSE" in names and "THIRD-PARTY-NOTICES.md" in names
    nuspec = ET.fromstring(archive.read(next(n for n in names if n.endswith(".nuspec"))))
    metadata = nuspec.find("{*}metadata")
    assert metadata.findtext("{*}id") == "MockProxyGateway"
    assert metadata.findtext("{*}license") == "MIT"
    deps = {d.attrib["id"] for d in metadata.findall(".//{*}dependency")}
    assert deps == {"Yarp.ReverseProxy", "JsonPath.Net"}, deps
    frameworks = {d.attrib["name"] for d in metadata.findall(".//{*}frameworkReference")}
    assert "Microsoft.AspNetCore.App" in frameworks, frameworks
    print("Validated package assembly, public documentation, MIT metadata, dependencies and framework reference.")

lock = json.loads((root / "src/MockProxyGateway.Core/packages.lock.json").read_text())["dependencies"]["net10.0"]
cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))
# These are the exact resolved runtime packages. Inspect their real restored nuspec metadata;
# never assume a later package version has the same terms.
for name, resolved in lock.items():
    version = resolved["resolved"]
    folder = cache / name.lower() / version
    nuspec = ET.parse(folder / (name.lower() + ".nuspec")).getroot().find("{*}metadata")
    license_element = nuspec.find("{*}license")
    url = nuspec.findtext("{*}licenseUrl")
    print(f"{name} {version}: license={license_element.text if license_element is not None else None}, "
          f"type={license_element.attrib.get('type') if license_element is not None else None}, url={url}")
    assert license_element is not None and license_element.attrib.get("type") == "expression" and license_element.text == "MIT", (
        "Runtime dependency license differs from reviewed MIT metadata; review explicitly", name, version
    )
    if license_element is not None and license_element.attrib.get("type") == "file":
        license_path = folder / license_element.text
        assert license_path.is_file()
        print(license_path.read_text(encoding="utf-8")[:12000])
