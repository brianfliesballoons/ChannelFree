"""Add (or replace) a version entry in manifest.json — the file Jellyfin reads from the plugin repository URL."""
import datetime, hashlib, json, sys
version, zip_path, url, meta_path = sys.argv[1:5]
meta = json.load(open(meta_path))
checksum = hashlib.md5(open(zip_path, "rb").read()).hexdigest()
manifest = json.load(open("manifest.json"))
pkg = manifest[0]
entry = {
    "version": version,
    "changelog": meta.get("changelog", ""),
    "targetAbi": meta["targetAbi"],
    "sourceUrl": url,
    "checksum": checksum,
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}
pkg["versions"] = [entry] + [v for v in pkg["versions"] if v["version"] != version]
pkg["versions"].sort(key=lambda v: [int(x) for x in v["version"].split(".")], reverse=True)
json.dump(manifest, open("manifest.json", "w"), indent=2)
open("manifest.json", "a").write("\n")
print("manifest updated:", version, checksum)
