using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
namespace RmsLink.Shared;
// IShellLinkW + IPersistFile keep both shortcut names and target paths in UTF-16.
public sealed record ShortcutInfo(string Target,string Arguments,string WorkingDirectory,string Icon,int IconIndex);
public static class ShortcutFile
{
    static object Open(string existing=null)
    {
        var obj=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
        try{if(existing!=null)((IPersistFile)obj).Load(Path.GetFullPath(existing),0);return obj;}
        catch{Marshal.FinalReleaseComObject(obj);throw;}
    }
    public static ShortcutInfo Read(string path)
    {
        var obj=Open(path);try{var link=(IShellLinkW)obj;var target=new StringBuilder(32768);var args=new StringBuilder(32768);var folder=new StringBuilder(32768);var icon=new StringBuilder(32768);
            link.GetPath(target,target.Capacity,IntPtr.Zero,4);link.GetArguments(args,args.Capacity);link.GetWorkingDirectory(folder,folder.Capacity);link.GetIconLocation(icon,icon.Capacity,out int index);
            return new(target.ToString(),args.ToString(),folder.ToString(),icon.ToString(),index);
        }finally{Marshal.FinalReleaseComObject(obj);}
    }
    public static void Create(string path,string target,string description,string icon,string arguments="",string workingDirectory=null)
    {
        var obj=Open();try{var link=(IShellLinkW)obj;link.SetPath(target);link.SetDescription(description);link.SetArguments(arguments);link.SetWorkingDirectory(workingDirectory??Path.GetDirectoryName(target));link.SetIconLocation(icon,0);((IPersistFile)obj).Save(Path.GetFullPath(path),true);}
        finally{Marshal.FinalReleaseComObject(obj);}
    }
    public static void Brand(string path,string description,string icon)
    {
        var obj=Open(path);try{var link=(IShellLinkW)obj;link.SetDescription(description);link.SetIconLocation(icon,0);((IPersistFile)obj).Save(Path.GetFullPath(path),true);}
        finally{Marshal.FinalReleaseComObject(obj);}
    }
    [ComImport,Guid("000214F9-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,int max,IntPtr findData,uint flags);
        void GetIDList(out IntPtr list);void SetIDList(IntPtr list);
        void GetDescription([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder text,int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder text,int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetArguments([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder text,int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short key);void SetHotkey(short key);void GetShowCmd(out int command);void SetShowCmd(int command);
        void GetIconLocation([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,int max,out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path,int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path,uint reserved);
        void Resolve(IntPtr window,uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
