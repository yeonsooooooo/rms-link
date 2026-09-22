using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RmsLink;
var folder=Path.Combine(Path.GetTempPath(),"rmslink-transport-"+Guid.NewGuid());
Directory.CreateDirectory(folder);
try {
    // No live account, connection, customer config, or Windows UI is touched.
    var cfg=new AppConfig {HotelId="test",AutoUpdate=false,ServerUrl="https://example.invalid",DeviceSecret=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(new string('a',64)),null,DataProtectionScope.CurrentUser))};
    var fixture=new TransportFixture();
    using(var sink=new ControlSink(cfg,fixture,folder)) {
        sink.Enqueue(new{test=true},Array.Empty<ParsedEvent>(),1);
        sink.Start();
        var deadline=DateTime.UtcNow.AddSeconds(20);
        while(sink.SentCount==0&&DateTime.UtcNow<deadline)await Task.Delay(100);
        if(sink.SentCount!=1||fixture.EnrollmentAttempts<2||sink.PendingCount!=0)throw new Exception("HTTP timeout stopped the transport instead of retrying and acknowledging the queued batch");
        fixture.Reject=true;
        sink.Enqueue(new{test=true},Array.Empty<ParsedEvent>(),1);
        deadline=DateTime.UtcNow.AddSeconds(12);
        while(!sink.LastError.Contains("REJECTED")&&DateTime.UtcNow<deadline)await Task.Delay(100);
        if(!sink.LastError.Contains("REJECTED")||Directory.GetFiles(Path.Combine(folder,"rejected"),"*.json").Length!=1)throw new Exception("Rejected observations must be retained and visibly reported");
    }
    Console.WriteLine("Windows transport passed: timeout retry, encrypted queue replay, acknowledgement, rejected-batch preservation.");
    return 0;
} finally {Directory.Delete(folder,true);}
sealed class TransportFixture:HttpMessageHandler {
    public int EnrollmentAttempts;
    public bool Reject;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
        string body="{\"ok\":true}",path=request.RequestUri.AbsolutePath;
        if(path=="/agent/enroll"&&Interlocked.Increment(ref EnrollmentAttempts)==1)throw new TaskCanceledException("Simulated HTTP timeout");
        if(path=="/agent/observations") {
            if(Reject)return new(HttpStatusCode.BadRequest){Content=new StringContent("{\"error\":\"rejected\"}")};
            using var original=JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            body=JsonSerializer.Serialize(new{ack=original.RootElement.GetProperty("id").GetString()});
        }
        return new(HttpStatusCode.OK){Content=new StringContent(body)};
    }
}
