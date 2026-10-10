"""Release notes: this version's changelog line + install instructions."""
import json, os
m = json.load(open("meta.json"))
latest = m.get("changelog", "").split(" 0.")[0].strip()  # changelog lists newest first
repo = os.environ.get("GITHUB_REPOSITORY", "scooperman912-prog/ChannelFree")
abi = ".".join(m["targetAbi"].split(".")[:2])
print(latest)
print()
print(f"**Install:** Jellyfin → Dashboard → Plugins → Repositories → add `https://raw.githubusercontent.com/{repo}/main/manifest.json`, "
      f"then install **Channel Free** from the Catalog and restart Jellyfin. Requires Jellyfin {abi}+. Setup steps are in the README.")
