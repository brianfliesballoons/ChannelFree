"""Fail the release if the tag doesn't match meta.json + the .csproj version."""
import json, re, sys
tag = sys.argv[1]
meta = json.load(open("meta.json"))["version"]
proj = re.search(r"<AssemblyVersion>([^<]+)</AssemblyVersion>", open("Jellyfin.Plugin.QuasiTV.csproj").read()).group(1)
if not (tag == meta == proj):
    sys.exit(f"Version mismatch: tag {tag}, meta.json {meta}, csproj {proj}. Tag releases like v{meta}.")
print("version ok:", tag)
