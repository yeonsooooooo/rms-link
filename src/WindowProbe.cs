using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
namespace RmsLink;
public sealed record ApplicationDetails(string Identity,string Vendor,string Product,string Version);
public sealed record WindowCandidate(long Handle,string Process,string Title,int X,int Y,int W,int H,bool Minimized)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Executable {get;init;}="";
    [System.Text.Json.Serialization.JsonIgnore] public int ProcessId {get;init;}
}
public static class WindowProbe
{
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd,out RECT rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd,ref POINT point);
    [StructLayout(LayoutKind.Sequential)] struct POINT {public int X,Y;}
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,IntPtr extra);
    delegate bool EnumProc(IntPtr hwnd,IntPtr extra);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint id);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd,System.Text.StringBuilder text,int size);
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern bool SwitchDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd,int command);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd,int command);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out int value,int size);
    [StructLayout(LayoutKind.Sequential)] struct RECT {public int L,T,R,B;}
    public static bool DesktopAvailable(){var d=OpenInputDesktop(0,false,0x0100);if(d==IntPtr.Zero)return false;try{return SwitchDesktop(d);}finally{CloseDesktop(d);}}
    public static void Activate(WindowCandidate w){ShowWindow(new(w.Handle),9);SetForegroundWindow(new(w.Handle));}
    public static void Restore(WindowCandidate w)=>ShowWindowAsync(new(w.Handle),9);
    public static ApplicationDetails Describe(WindowCandidate w, string vendor)
    {
        var file=new FileInfo(w.Executable);
        var version=FileVersionInfo.GetVersionInfo(w.Executable);
        var identity=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(w.Executable.ToUpperInvariant()+"|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks+"|"+version.FileVersion))).ToLowerInvariant();
        return new(identity,vendor,version.ProductName??"",version.FileVersion??"");
    }
    public static List<WindowCandidate> All()
    {
        var list=new List<WindowCandidate>();
        EnumWindows((h,_)=>{
            try {
                if(!IsWindowVisible(h)||!GetCaptureRect(h,out var r)||r.R-r.L<20||r.B-r.T<20)return true;
                DwmGetWindowAttribute(h,14,out int cloaked,4);if(cloaked!=0)return true;
                var title=new System.Text.StringBuilder(512);GetWindowText(h,title,512);if(title.Length==0)return true;
                GetWindowThreadProcessId(h,out uint pid);using var p=Process.GetProcessById((int)pid);
                string path="";try{path=p.MainModule?.FileName??"";}catch{}
                list.Add(new(h.ToInt64(),p.ProcessName,title.ToString()[..Math.Min(title.Length,160)],r.L,r.T,r.R-r.L,r.B-r.T,IsIconic(h)){Executable=path,ProcessId=(int)pid});
            }catch{}return true;
        },IntPtr.Zero);
        return list;
    }
    public static List<WindowCandidate> Find()=>All().Where(w=>!w.Process.StartsWith("RmsLink",StringComparison.OrdinalIgnoreCase)&&System.Text.RegularExpressions.Regex.IsMatch(w.Process+" "+w.Title,@"keyte[ch]*|키텍|객실관리|객실 관리|\bRMS\w*|ROMASYS|UBSYS|SeeReal|MIRAE",System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Take(10).ToList();
    public static List<WindowCandidate> Find(SelectedApplication app)
    {
        if(app==null)return new();
        // Executable identity survives a restart. Never fall back to a different application.
        var matching=All().Where(w=>!string.IsNullOrEmpty(app.Executable) && string.Equals(w.Executable,app.Executable,StringComparison.OrdinalIgnoreCase)).ToList();
        var exact=matching.FirstOrDefault(w=>w.Handle==app.Handle&&w.ProcessId==app.ProcessId);
        if(exact!=null)return new(){exact};
        if(matching.Count>1){var title=matching.Where(w=>w.Title==app.Title).ToList();if(title.Count==1)return title;}
        return matching.Take(10).ToList();
    }
    public static bool Unobscured(WindowCandidate selected)
    {
        var area=new Rectangle(selected.X,selected.Y,selected.W,selected.H);
        if(!SystemInformation.VirtualScreen.Contains(area))return false;
        // Include untitled popup windows too: they must never leak into a screen capture.
        bool visible=false;
        EnumWindows((handle,_)=>{
            if(handle.ToInt64()==selected.Handle){visible=true;return false;}
            if(!IsWindowVisible(handle)||IsIconic(handle)||!GetWindowRect(handle,out var r))return true;
            DwmGetWindowAttribute(handle,14,out int cloaked,4);if(cloaked!=0)return true;
            return !area.IntersectsWith(new Rectangle(r.L,r.T,Math.Max(0,r.R-r.L),Math.Max(0,r.B-r.T)));
        },IntPtr.Zero);
        return visible;
    }
    static bool GetCaptureRect(IntPtr handle,out RECT r)
    {
        // Capture the client area: invisible resize borders contain pixels from other apps.
        if(IsIconic(handle))return GetWindowRect(handle,out r);
        if(!GetClientRect(handle,out r))return false;var origin=new POINT();
        if(!ClientToScreen(handle,ref origin))return false;
        r.R+=origin.X;r.B+=origin.Y;r.L+=origin.X;r.T+=origin.Y;return true;
    }
    public static bool SameGeometry(WindowCandidate selected)=>GetWindowThreadProcessId(new(selected.Handle),out var pid)>0 && pid==selected.ProcessId && GetCaptureRect(new(selected.Handle),out var r)&&r.L==selected.X&&r.T==selected.Y&&r.R-r.L==selected.W&&r.B-r.T==selected.H;

    public static List<string> ReadAccessibleRows(WindowCandidate window) => ReadAccessible(window).Lines;

    public static AccessibleReadResult ReadAccessible(WindowCandidate window,Rectangle? region=null)
    {
        var root=AutomationElement.FromHandle(new IntPtr(window.Handle));
        var clip=region.HasValue
            ? new System.Windows.Rect(window.X+region.Value.X,window.Y+region.Value.Y,region.Value.Width,region.Value.Height)
            : new System.Windows.Rect(window.X,window.Y,window.W,window.H);
        var result=new AccessibleReadResult();
        var fragments=new List<AccessibleFragment>();
        // Raw view includes cells hidden by the provider's ControlView classification.
        var walker=TreeWalker.RawViewWalker;
        var watch=Stopwatch.StartNew();int visited=0,failures=0;bool limited=false;
        bool Budget() { if(visited>=4000 || watch.ElapsedMilliseconds>=1200){limited=true;return false;}return true; }
        bool Inside(System.Windows.Rect rect) => !rect.IsEmpty && rect.Width>0 && rect.Height>0 && clip.Contains(rect);
        void Walk(AutomationElement node,int depth,string scope,bool inRow)
        {
            if(!Budget())return;
            visited++;
            try {
                var current=node.Current;
                if(current.IsPassword)return;
                var type=current.ControlType;
                bool row=type==ControlType.DataItem || type==ControlType.ListItem;
                if(!inRow && (row || type==ControlType.Table || type==ControlType.DataGrid || type==ControlType.List || type==ControlType.Pane || type==ControlType.Group))scope=visited.ToString();
                int before=fragments.Count+result.Lines.Count;
                // Traverse nested wrappers, even when their own Name or rectangle is empty.
                var child=walker.GetFirstChild(node);
                if(child!=null && depth>=24)limited=true;
                while(child!=null && depth<24 && Budget()) {
                    Walk(child,depth+1,scope,inRow||row);
                    try {child=walker.GetNextSibling(child);}catch {failures++;break;}
                }
                if(current.IsOffscreen || !Inside(current.BoundingRectangle))return;
                if(before!=fragments.Count+result.Lines.Count)return; // Do not duplicate parent row names.
                if((type==ControlType.Document || type==ControlType.Edit) && node.TryGetCurrentPattern(TextPattern.Pattern,out var textPattern)) {
                    var textLines=((TextPattern)textPattern).GetVisibleRanges().Take(100)
                        .SelectMany(range=>range.GetText(32000).Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)).Where(line=>line.Length<=1000).Take(300).ToList();
                    if(type==ControlType.Edit && textLines.Count==1) {
                        var r=current.BoundingRectangle;
                        fragments.Add(new(scope,new(textLines[0],r.X,r.Y,r.Width,r.Height)));
                    } else result.Lines.AddRange(textLines);
                    if(before!=fragments.Count+result.Lines.Count)return;
                }
                if(type==ControlType.Text || type==ControlType.Edit || type==ControlType.Custom || row) {
                    string text=current.Name;
                    // A grid cell's Name may be its column heading; Value contains the actual data.
                    if(node.TryGetCurrentPattern(ValuePattern.Pattern,out var value) && !string.IsNullOrWhiteSpace(((ValuePattern)value).Current.Value))text=((ValuePattern)value).Current.Value;
                    if(!string.IsNullOrWhiteSpace(text) && text.Length<=1000) {
                        var r=current.BoundingRectangle;
                        fragments.Add(new(scope,new(text,r.X,r.Y,r.Width,r.Height)));
                    }
                }
            }catch {failures++;} // One disappearing/broken cell must not discard every other row.
        }
        Walk(root,0,"root",false);
        result.Lines.AddRange(AccessibleRowLayout.JoinFragments(fragments));
        var lines=result.Lines.Where(x=>x.Length<=1000).Distinct().ToList();
        if(lines.Count>300)limited=true;
        result.Lines=lines.Take(300).ToList();
        if(limited)result.Warnings.Add("UIA_PARTIAL: 직접 읽기 탐색 한도에 도달했습니다. 이벤트 내역 창 또는 로그 영역을 선택하세요. 나머지는 OCR로 확인합니다");
        if(failures>0)result.Warnings.Add($"UIA_PARTIAL: {failures}개 요소를 읽지 못했습니다. 확보한 행과 OCR을 사용합니다");
        return result;
    }
}

public sealed class AccessibleReadResult
{
    public List<string> Lines {get;set;}=new();
    public List<string> Warnings {get;}=new();
}
