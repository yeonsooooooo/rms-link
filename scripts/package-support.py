"""Deterministic standalone Windows support ZIP; contains no application credentials."""
from pathlib import Path
import zipfile
root=Path(__file__).resolve().parents[1]
with zipfile.ZipFile(root/'assets/RmsLink-Diagnostics.zip','w',zipfile.ZIP_DEFLATED) as archive:
    for name in ['collect-logs.cmd','collect-logs.ps1']:
        entry=zipfile.ZipInfo(name,(2026,1,1,0,0,0))
        entry.compress_type=zipfile.ZIP_DEFLATED
        archive.writestr(entry,(root/name).read_bytes())
