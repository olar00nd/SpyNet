using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Policy;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DataMaker
{
    class Program
    {
        private static readonly string serverUrl = "ws://93.170.131.143:8080";
        private static ClientWebSocket ws;
        private static bool connected;
        private static readonly string ramdirPath = @"C:\ramdir";
        private static readonly string secretCfgPath = Path.Combine(ramdirPath, "secret.cfg");
        private static string baseboardSerial;
        private static string oldEncKey = "";

        static async Task Main(string[] args)
        {
            if (!Directory.Exists(ramdirPath)) Directory.CreateDirectory(ramdirPath);
            LoadEncKey();
            baseboardSerial = GetBaseboardSerial();
            Console.WriteLine("Open-Source Client | DataMaker | Official Build | 1.1.0");
            Console.WriteLine("Support: https://t.me/RomanShpilka");
            Console.WriteLine("Press ENTER to connect...");
            Console.ReadLine();
            OpenBrowser("https://discord.gg/2Pv8gFAU7M");
            await Connect();
            if (!connected) { Console.WriteLine("Failed to connect."); return; }
            Console.WriteLine("Connected to API Server");
            _ = Task.Run(async () => await RecvLoop());
            _ = Task.Run(async () => await HeartbeatLoop());
            while (connected)
            {
                Console.WriteLine("Type SITE or EXIT:");
                var inp = Console.ReadLine()?.Trim().ToUpper();
                if (inp == "SITE") await RequestSite();
                else if (inp == "EXIT")
                {
                    await StopSession();
                    connected = false;
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "User exit", CancellationToken.None);
                }
            }
        }

        static async Task Connect()
        {
            ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(serverUrl), CancellationToken.None);
                connected = (ws.State == WebSocketState.Open);
            }
            catch (Exception ex) { Console.WriteLine("Conn err => " + ex.Message); connected = false; }
        }

        static async Task RecvLoop()
        {
            var buffer = new byte[4096];
            while (connected && ws.State == WebSocketState.Open)
            {
                try
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) { Console.WriteLine("Server closed."); connected = false; break; }
                    var txt = Encoding.UTF8.GetString(buffer, 0, r.Count);
                    await HandleMsg(txt);
                }
                catch (Exception ex) { Console.WriteLine("Recv err => " + ex.Message); connected = false; break; }
            }
        }

        static async Task HandleMsg(string raw)
        {
            dynamic d = JsonConvert.DeserializeObject(raw);
            if (d == null) return;
            string action = d.action;
            if (action == "REQUEST_PC_DATA") await SendPCData();
            else if (action == "CONNECTION_STATUS") ConnStatus(d);
            else if (action == "SITE_STATUS") SiteStatus(d);
        }

        static async Task SendPCData()
        {
            var ip = GetIP();
            var sp = GetPCSpecs();
            var obj = new { action = "SEND_PC_DATA", baseboard_serial = baseboardSerial, real_ip = ip, pc_specs = sp, old_enc_key = oldEncKey };
            var js = JsonConvert.SerializeObject(obj);
            await Send(js);
        }

        static void ConnStatus(dynamic d)
        {
            string s = d.status;
            string m = d.message;
            Console.WriteLine("[CLIENT] " + m);
            if (s == "SUCCESS")
            {
                if (d.new_enc_key != null)
                {
                    string ne = d.new_enc_key;
                    oldEncKey = ne;
                    SaveEncKey();
                }
            }
            else
            {
                Console.WriteLine("Conn fail => closing");
                connected = false;
                ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Fail", CancellationToken.None).Wait();
            }
        }

        static void SiteStatus(dynamic d)
        {
            string s = d.status;
            string m = d.message;
            Console.WriteLine("[CLIENT] => " + m);
            if (s == "SUCCESS")
            {
                string url = d.url;
                Console.WriteLine("[CLIENT] Opening => " + url);
                OpenBrowser(url);
            }
        }

        static async Task RequestSite()
        {
            var pl = new { action = "REQUEST_SITE" };
            var js = JsonConvert.SerializeObject(pl);
            await Send(js);
        }

        static async Task StopSession()
        {
            var pl = new { action = "STOP_SESSION" };
            var js = JsonConvert.SerializeObject(pl);
            await Send(js);
        }

        static async Task Send(string msg)
        {
            try
            {
                var b = Encoding.UTF8.GetBytes(msg);
                await ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex) { Console.WriteLine("Send err => " + ex.Message); connected = false; }
        }

        static async Task HeartbeatLoop()
        {
            while (connected)
            {
                await Task.Delay(10000);
                var obj = new { action = "HEARTBEAT", baseboard_serial = baseboardSerial };
                var js = JsonConvert.SerializeObject(obj);
                await Send(js);
            }
        }

        static void OpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex) { Console.WriteLine("Browser => " + ex.Message); }
        }

        static void LoadEncKey()
        {
            try
            {
                if (File.Exists(secretCfgPath)) oldEncKey = File.ReadAllText(secretCfgPath).Trim();
            }
            catch { }
        }
        static void SaveEncKey()
        {
            try
            {
                File.WriteAllText(secretCfgPath, oldEncKey);
            }
            catch (Exception ex) { Console.WriteLine("Save Key => " + ex.Message); }
        }

        static string GetIP()
        {
            try
            {
                using (var hc = new HttpClient())
                {
                    var ip = hc.GetStringAsync("https://api.ipify.org").Result;
                    return ip.Trim();
                }
            }
            catch { return "127.0.0.1"; }
        }

        static string GetPCSpecs()
        {
            try
            {
                var sb = new StringBuilder();
                using (var mc = new ManagementClass("Win32_Processor"))
                {
                    foreach (ManagementObject mo in mc.GetInstances())
                    {
                        var nm = mo["Name"]?.ToString();
                        if (!string.IsNullOrEmpty(nm)) { sb.Append("CPU:" + nm.Trim() + "; "); break; }
                    }
                }
                using (var s = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory"))
                {
                    long total = 0;
                    foreach (ManagementObject mo in s.Get())
                    {
                        var c = mo["Capacity"];
                        if (c != null) total += Convert.ToInt64(c);
                    }
                    if (total > 0) sb.Append("RAM:" + (total / (1024 * 1024 * 1024)) + "GB; ");
                }
                return sb.ToString();
            }
            catch { return "Unknown"; }
        }

        static string GetBaseboardSerial()
        {
            try
            {
                using (var ms = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard"))
                {
                    foreach (ManagementObject mo in ms.Get())
                    {
                        var sn = mo["SerialNumber"]?.ToString();
                        if (!string.IsNullOrEmpty(sn)) return sn.Trim();
                    }
                }
            }
            catch { }
            return Guid.NewGuid().ToString();
        }
    }
}
