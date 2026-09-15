using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
namespace RmsLink;
public sealed record WindowCandidate(long Handle,string Process,string Title,int X,int Y,int W,int H,bool Minimized);
public static class WindowProbe
{
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [StructLayout(LayoutKind.Sequential)] struct RECT {public int L,T,R,B;}
    public static bool DesktopAvailable(){var d=OpenInputDesktop(0,false,0x0100);if(d==IntPtr.Zero)return false;CloseDesktop(d);return true;}
    public static List<WindowCandidate> Find()
    {
        var list=new List<WindowCandidate>();
        foreach(var p in Process.GetProcesses()) using(p) try {
            if(p.MainWindowHandle==IntPtr.Zero || p.ProcessName.StartsWith("RmsLink",StringComparison.OrdinalIgnoreCase))continue;
            string t=p.MainWindowTitle;
            if(!System.Text.RegularExpressions.Regex.IsMatch(p.ProcessName+" "+t,@"keyte[ch]*|키텍|객실관리|객실 관리|\bRMS\w*|ROMASYS|UBSYS|SeeReal|MIRAE|A/S Call",System.Text.RegularExpressions.RegexOptions.IgnoreCase))continue;
            if(GetWindowRect(p.MainWindowHandle,out var r))list.Add(new(p.MainWindowHandle.ToInt64(),p.ProcessName,t[..Math.Min(t.Length,160)],r.L,r.T,r.R-r.L,r.B-r.T,IsIconic(p.MainWindowHandle)));
        }catch{}
        return list.Take(10).ToList();
    }
    public static List<string> ReadAccessibleRows(WindowCandidate window)
    {
        var root=AutomationElement.FromHandle(new IntPtr(window.Handle));
        var list=new List<string>();
        var walker=TreeWalker.ControlViewWalker;var queue=new Queue<(AutomationElement,int)>();queue.Enqueue((root,0));int count=0;
        // Bounded traversal of the selected RMS window; provider failures are reported by caller.
        while(queue.Count>0 && count++<700) {
            var (e,depth)=queue.Dequeue();
            var current=e.Current;
            if(!current.IsOffscreen && current.ControlType==ControlType.DataItem) {
                var words=new List<string>(); if(!string.IsNullOrWhiteSpace(current.Name))words.Add(current.Name);
                var child=walker.GetFirstChild(e);int cells=0;
                while(child!=null && cells++<20){var n=child.Current.Name;if(!string.IsNullOrWhiteSpace(n)&&!words.Contains(n))words.Add(n);child=walker.GetNextSibling(child);}
                if(words.Count>0)list.Add(string.Join(" ",words));
                continue;
            }
            if(!current.IsOffscreen && current.ControlType==ControlType.Text && !string.IsNullOrWhiteSpace(current.Name))list.Add(current.Name);
            if(depth<8){var child=walker.GetFirstChild(e);int children=0;while(child!=null && children++<100){queue.Enqueue((child,depth+1));child=walker.GetNextSibling(child);}}
        }
        return list.Where(x=>x.Length<=1000).Distinct().Take(300).ToList();
    }
}
