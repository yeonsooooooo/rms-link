using System.Runtime.InteropServices;
using System.Drawing.Imaging;
namespace RmsLink;
public sealed class WindowCapture:IDisposable
{
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd,IntPtr hdc,uint flags);
    Task<Bitmap> pending; bool abandoned;
    static Bitmap Print(WindowCandidate w)
    {
        var image=new Bitmap(w.W,w.H,PixelFormat.Format32bppArgb);
        try {using var g=Graphics.FromImage(image);g.Clear(Color.Black);var hdc=g.GetHdc();bool ok;
            try{ok=PrintWindow(new(w.Handle),hdc,3);}finally{g.ReleaseHdc(hdc);}
            if(!ok)throw new Exception("WINDOW_CAPTURE_EMPTY: 앱이 창 캡처를 지원하지 않습니다");
            return image;
        }catch{image.Dispose();throw;}
    }
    static bool HasContent(Bitmap bmp)
    {
        var first=bmp.GetPixel(bmp.Width/2,bmp.Height/2);int differences=0;
        for(int y=8;y<bmp.Height;y+=Math.Max(1,bmp.Height/30))for(int x=8;x<bmp.Width;x+=Math.Max(1,bmp.Width/30))
            if(bmp.GetPixel(x,y).ToArgb()!=first.ToArgb())differences++;
        return differences>10;
    }
    public async Task<(Bitmap Image,string Method)> Read(WindowCandidate w)
    {
        if(w.Minimized)throw new Exception("WINDOW_MINIMIZED: 키텍 창을 복원해 주세요");
        if(w.W<20||w.H<20||w.W>8000||w.H>8000||((long)w.W*w.H)>20000000)throw new Exception("REGION_INVALID: 키텍 창 크기를 줄여 주세요");
        if(WindowProbe.Unobscured(w)) {
            var shot=CaptureService.Capture(new(w.X,w.Y,w.W,w.H));
            if(WindowProbe.Unobscured(w)&&WindowProbe.SameGeometry(w))return (shot,"visible-window");
            shot.Dispose();throw new Exception("WINDOW_OCCLUDED: 캡처 중 키텍 창이 이동하거나 다른 창이 겹쳤습니다");
        }
        if(pending!=null&&!pending.IsCompleted)throw new Exception("WINDOW_CAPTURE_TIMEOUT: 키텍 앱이 창 캡처에 응답하지 않습니다");
        if(pending!=null){if(pending.IsCompletedSuccessfully)pending.Result.Dispose();else _=pending.Exception;pending=null;}
        if(abandoned)throw new Exception("WINDOW_CAPTURE_TIMEOUT: 수집 종료 중");
        pending=Task.Run(()=>Print(w));
        if(await Task.WhenAny(pending,Task.Delay(1200))!=pending)throw new Exception("WINDOW_CAPTURE_TIMEOUT: 키텍 창을 앞으로 가져와 주세요");
        Bitmap image;try{image=await pending;}finally{pending=null;}
        if(!WindowProbe.SameGeometry(w)){image.Dispose();throw new Exception("WINDOW_OCCLUDED: 캡처 중 선택한 창이 바뀌었습니다");}
        if(!HasContent(image)){image.Dispose();throw new Exception("WINDOW_CAPTURE_EMPTY: 키텍 창을 앞으로 가져와 주세요");}
        return (image,"print-window");
    }
    public void Dispose(){abandoned=true;var task=pending;pending=null;if(task!=null)_=task.ContinueWith(t=>{if(t.IsCompletedSuccessfully)t.Result.Dispose();else _=t.Exception;});}
}
