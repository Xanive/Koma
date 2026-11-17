"""Stage the exact audited Windows engine files and their corresponding sources."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request
import zipfile


ROOT = Path(__file__).resolve().parent
LOCK = ROOT / "msys2-engine.lock.json"


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def fetch(url, path, expected):
    if not url.startswith("https://"):
        raise ValueError(f"Expected an HTTPS download URL: {url}")
    if not path.is_file():
        path.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as pending:
            scratch = Path(pending.name)
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "Koma release build"})
            with urllib.request.urlopen(request, timeout=90) as source, scratch.open("wb") as output:
                shutil.copyfileobj(source, output)
            if sha256(scratch) != expected:
                raise ValueError(f"SHA-256 mismatch for {path.name}")
            scratch.replace(path)
        finally:
            scratch.unlink(missing_ok=True)
    elif sha256(path) != expected:
        raise ValueError(f"SHA-256 mismatch for cached {path}. Remove that cache file and retry.")
    return path


def runtime_files(package, archive, engine, notices, seven_zip):
    required = {item["name"].casefold(): item["sha256"] for item in package["binaries"]}
    copied = set()
    package_notices = notices / package["name"]
    package_notices.mkdir(exist_ok=True)
    dec = subprocess.check_output([seven_zip, "e", "-so", "--", str(archive)])
    with tarfile.open(fileobj=io.BytesIO(dec), mode="r:") as stream:
        for entry in stream:
            if not entry.isfile():
                continue
            path = PurePosixPath(entry.name)
            if path.parent == PurePosixPath("ucrt64/bin") and path.name.casefold() in required:
                key = path.name.casefold()
                data = stream.extractfile(entry).read()
                if hashlib.sha256(data).hexdigest() != required[key]:
                    raise ValueError(f"SHA-256 mismatch for {path.name} in {archive.name}")
                (engine / key).write_bytes(data)
                copied.add(key)
            elif path.parts[:3] == ("ucrt64", "share", "licenses"):
                relative = path.parts[3:]
                if not relative or ".." in relative or any(":" in part or "\\" in part for part in relative):
                    raise ValueError(f"Unsafe license path {path}")
                target = package_notices.joinpath(*relative)
                target.parent.mkdir(exist_ok=True, parents=True)
                target.write_bytes(stream.extractfile(entry).read())
            elif entry.name in (".BUILDINFO", ".PKGINFO"):
                (package_notices / entry.name.lstrip(".")).write_bytes(stream.extractfile(entry).read())
    if set(required) != copied:
        raise ValueError(f"{archive.name} is missing: {sorted(set(required) - copied)}")
    build = package_notices / "BUILDINFO"
    recipe = f'pkgbuild_sha256sum = {package["source"]["recipeSha256"]}'
    if not build.is_file() or recipe not in build.read_text(encoding="utf-8").splitlines():
        raise ValueError(f"The build recipe recorded by {archive.name} differs from the source lock")


def source_notices(package, archive, notices, seven_zip):
    folder = notices / package["name"]
    # Several packages rely on the distribution's common license store.
    # For the standalone player, copy the originals from their pinned source.
    if any(x.is_dir() for x in folder.iterdir()) or package["name"].endswith(("-libx264", "-rtmpdump")):
        return
    dec = subprocess.check_output([seven_zip, "e", "-so", "--", str(archive)])
    with tarfile.open(fileobj=io.BytesIO(dec), mode="r:") as source:
        for blob in source:
            if not blob.isfile() or not blob.name.endswith((".tar.gz", ".tar.xz", ".tar.bz2", ".tgz")):
                continue
            with tarfile.open(fileobj=source.extractfile(blob), mode="r:*") as inner:
                for license in inner:
                    path = PurePosixPath(license.name)
                    if (license.isfile() and len(path.parts) <= 3 and ".." not in path.parts
                            and not path.is_absolute() and ":" not in license.name
                            and re.match(r"^(COPYING|LICEN[CS]E|COPYRIGHT|NOTICE|AUTHORS)(?:$|[._-])", path.name, re.I)):
                        target = (folder / "source-notices").joinpath(*path.parts[-2:])
                        target.parent.mkdir(parents=True, exist_ok=True)
                        target.write_bytes(inner.extractfile(license).read())
    if not any(x.is_dir() for x in folder.iterdir()):
        raise ValueError(f"No license notice found for {package['name']}")


def downloads(lock, cache):
    tasks = {}
    replacements = {}
    for package in lock["packages"]:
        tasks[cache / "binary-packages" / package["archive"]] = (package["url"], package["sha256"])
        source = package["source"]
        tasks[cache / "source-packages" / source["archive"]] = (source["url"], source["sha256"])
        if override := source["replacementRecipe"]:
            # This known archived source package used the preceding PKGBUILD.
            # The exact compiled revision adds only an openssl-3 build dependency.
            filename = "ngtcp2-f6fe8c6a81c3.PKGBUILD"
            replacement = cache / filename
            tasks[replacement] = (override["url"], override["sha256"])
            replacements[package["name"]] = replacement
    for source in lock["additionalSourceInputs"]:
        tasks[cache / "source-packages" / source["source"]] = (source["url"], source["sha256"])

    def verify(item):
        path, (url, digest) = item
        return fetch(url, path, digest)

    with ThreadPoolExecutor(max_workers=8) as threads:
        verified = list(threads.map(verify, tasks.items()))
    sources = sorted(path for path in verified if path.parent.name == "source-packages")
    return sources, replacements


def create_source_zip(path, notices, sources, replacements, expected_name):
    scratch = path.with_suffix(".tmp")
    try:
        with zipfile.ZipFile(scratch, "w", zipfile.ZIP_STORED) as bundle:
            bundle.write(LOCK, LOCK.name)
            bundle.write(ROOT.parent / "docs" / "Engine-provenance.md", "BUILDING.md")
            bundle.writestr("UPSTREAM-SOURCE-MANIFEST.txt",
                            f"{expected_name}\nSee {LOCK.name} for exact versions and hashes.\n"
                            "Original source archives, upstream notices, and any replacement recipe are included.\n")
            for source in sources:
                bundle.write(source, "sources/" + source.name)
            for notice in sorted(notices.rglob("*")):
                if notice.is_file():
                    bundle.write(notice, (Path("licenses") / notice.relative_to(notices)).as_posix())
            for package, recipe in sorted(replacements.items()):
                bundle.write(recipe, f"recipes/{package}/PKGBUILD")
        with zipfile.ZipFile(scratch) as bundle:
            if failed := bundle.testzip():
                raise ValueError(f"Corresponding-source archive integrity check failed for {failed}")
        scratch.replace(path)
    finally:
        scratch.unlink(missing_ok=True)


def stage(args):
    lock = json.loads(LOCK.read_text(encoding="utf-8"))
    if lock["schema"] != 1 or not lock["packages"] or not lock["additionalSourceInputs"]:
        raise ValueError("Invalid MSYS2 engine lock")
    print(f"Verifying {len(lock['packages'])} binary packages and matching sources for {lock['engine']}…", flush=True)
    sources, replacements = downloads(lock, args.cache)
    args.engine.mkdir(parents=True, exist_ok=True)
    args.notices.mkdir(parents=True, exist_ok=True)
    for pkg in lock["packages"]:
        archive = args.cache / "binary-packages" / pkg["archive"]
        runtime_files(pkg, archive, args.engine, args.notices, args.seven_zip)
        source = args.cache / "source-packages" / pkg["source"]["archive"]
        source_notices(pkg, source, args.notices, args.seven_zip)
    for record in lock["supplementalNotices"]:
        original = ROOT / "licenses" / record["localName"]
        if sha256(original) != record["sha256"]:
            raise ValueError(f"SHA-256 mismatch for {original.name}")
        filename = args.notices / record["installedAs"]
        filename.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(original, filename)
    create_source_zip(args.sources, args.notices, sources, replacements, lock["engine"])
    print(f"Engine and {len(sources)} matching source archives staged.", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for path in ("cache", "engine", "notices", "sources"):
        parser.add_argument("--" + path, type=Path, required=True)
    parser.add_argument("--seven-zip", required=True)
    stage(parser.parse_args())
