"""Build private single-EXE installer using Windows-validated release artifacts."""
import argparse, hashlib, json, shutil, subprocess, zipfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('--artifacts',type=Path,required=True);p.add_argument('--bootstrap',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--dotnet',default='/Users/ys/.local/share/rmslink-dotnet/dotnet');args=p.parse_args()
report=json.loads((args.artifacts/'validation.json').read_text(encoding='utf-8-sig'));archive=args.artifacts/'RmsLink.zip'
if not report['passed'] or not report['runtime']['passed'] or hashlib.sha256(archive.read_bytes()).hexdigest()!=report['sha256']:raise SystemExit('Windows validation/artifact mismatch')
version=report['version'];work=ROOT/'.work/installer-payload'
if work.exists():shutil.rmtree(work)
work.mkdir(parents=True,exist_ok=True)
app=work/'versions'/version
if app.exists():shutil.rmtree(app)
app.mkdir(parents=True)
with zipfile.ZipFile(archive) as z:
    for name in z.namelist():
        if '..' in Path(name).parts or Path(name).is_absolute():raise SystemExit('Unsafe ZIP')
    z.extractall(app)
launcher=args.artifacts/'launcher/RmsLinkLauncher.exe'
if not launcher.exists():launcher=args.artifacts/'RmsLinkLauncher.exe'
shutil.copy2(launcher,work/'RmsLinkLauncher.exe');shutil.copy2(args.bootstrap,work/'bootstrap.json')
(work/'install.json').write_text(json.dumps({'version':version}))
payload=ROOT/'.work/InstallerPayload.zip'
with zipfile.ZipFile(payload,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for file in sorted(work.rglob('*')):
        if file.is_file():z.write(file,file.relative_to(work))
publish=ROOT/'.work/setup-publish'
subprocess.run([args.dotnet,'publish',str(ROOT/'installer/Installer.csproj'),'-c','Release','-r','win-x64','--self-contained','true','-p:PayloadPath='+str(payload),'-o',str(publish)],check=True)
args.output.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(publish/'RmsLinkLauncher.exe',args.output)
output={'version':version,'sha256':hashlib.sha256(args.output.read_bytes()).hexdigest(),'size':args.output.stat().st_size,'windowsAppRuntimeVerified':True,'installerExecutedOnWindows':False,'authenticodeSigned':False,'physicalKeytechVerified':False,'commit':report['commit']}
args.output.with_suffix('.json').write_text(json.dumps(output,indent=2)+'\n');print(json.dumps(output))
