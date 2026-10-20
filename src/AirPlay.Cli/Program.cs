using System.Runtime.InteropServices;
using AirPlay.Core;
using AirPlay.Core.Identity;

// Development host (console). Usage: AirPlay.Cli [--name "顯示名稱"] [-v] [--no-record]
string? name = null;
var verbose = false;
var record = true;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
    else if (args[i] is "-v" or "--verbose") verbose = true;
    else if (args[i] == "--no-record") record = false;
}

// Console encoding: in a console window, switch it to UTF-8. When output is piped
// (e.g. "| Tee-Object"), Windows PowerShell 5.1 decodes it with the console's original code page
// (Big5 on Traditional Chinese Windows), so keep writing in that code page or the text is garbled.
if (!Console.IsOutputRedirected) Console.OutputEncoding = System.Text.Encoding.UTF8;

using var log = new FileLog(Path.Combine(Environment.CurrentDirectory, "logs"), "airplay", Console.WriteLine);
void Log(string msg) => log.Write(msg);

Log($"記錄檔: {log.Path}");
var id = DeviceIdentity.LoadOrCreate(name);
Log($"裝置名稱: {id.Name}");
Log($"Device ID: {id.DeviceId}");
Log($"身分檔案: {DeviceIdentity.DefaultPath}");

var recordDir = Path.Combine(Environment.CurrentDirectory, "recordings");
var receiver = new AirPlayReceiver(id, new ReceiverOptions
{
    Verbose = verbose,
    MirrorSinkFactory = record ? _ => new H264Recorder(recordDir, Log) : null,
}, Log);

// ---- shutdown handling ----
// Every way of closing must withdraw the mDNS services, or iPhones keep listing this PC:
//   Ctrl+C / Ctrl+Break -> CancelKeyPress
//   window X button      -> SIGHUP (CTRL_CLOSE_EVENT): Windows kills the process ~5 s after the handler returns
//   logoff / shutdown    -> SIGTERM
var stopRequested = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopRequested.TrySetResult(); };
void OnCloseSignal(PosixSignalContext ctx)
{
    ctx.Cancel = true;
    receiver.Stop(TimeSpan.FromSeconds(4)); // must finish before Windows kills the process
}
using var onClose = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnCloseSignal);
using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnCloseSignal);

await receiver.StartAsync();
Log("在 iPhone 打開「控制中心 → 螢幕鏡像輸出」，選這台電腦。按 Ctrl+C 或關閉視窗結束。");
await Task.WhenAny(stopRequested.Task, receiver.Completion);
await receiver.DisposeAsync();
