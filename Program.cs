using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscordRPC;
using Newtonsoft.Json;
using Spectre.Console;

namespace DataMaker
{
    class Program
    {
        private static readonly string serverUrl = "ws://34.147.8.12:8081"; //We add new stable WS server | 4/17/2025))
        private static ClientWebSocket ws;
        private static bool connected;
        private static readonly string ramdirPath = @"C:\ramdir"; //Your keys are stored here! Under no circumstances should you share them, as your account may be permanently suspended!
        private static readonly string secretCfgPath = Path.Combine(ramdirPath, "secret.cfg"); //Your keys are stored here! Under no circumstances should you share them, as your account may be permanently suspended!
        private static string baseboardSerial;
        private static string oldEncKey = string.Empty;
        private static DiscordRpcClient discordClient;
        private static HUDState hud = new HUDState();

        static async Task Main(string[] args)
        {
            Directory.CreateDirectory(ramdirPath);

            InitializeDiscordRPC();
            LoadEncKey();
            baseboardSerial = GetBaseboardSerial();

            var hudTask = Task.Run(() => HUDLoop());

            AnsiConsole.Write(new FigletText("Filaza Client V2.2.0").Color(Color.Red)); //A quick note on how we name our versions: we pick random words—even ones that don’t even exist—and append “vX.X.X.” Each version comes with its own new features. In this update, only the servers have been upgraded—nothing else has changed. In the next release, we’ll strive to implement a HUD to make the software easier to use. As for the server, we’re already working on bug fixes; by the time you read this, everything might already be ready.
            AnsiConsole.Write(new Markup("[bold yellow]Support:[/] [link=https://t.me/ne_kenti]Telegram[/]\n")); //Manager

            await CountdownTimer(15);

            OpenBrowser("https://discord.gg/2Pv8gFAU7M"); //We have a discord))
            await Connect();

            if (!connected)
            {
                AnsiConsole.Markup("[red]Connection failed![/]");
                return;
            }

            _ = Task.Run(() => RecvLoop());
            _ = Task.Run(() => HeartbeatLoop());

            while (connected)
            {
                var inp = AnsiConsole.Ask<string>("[bold]Type SITE or EXIT:[/]").Trim().ToUpper();

                if (inp == "SITE") await RequestSite();
                else if (inp == "EXIT") await Shutdown();
            }

            await hudTask;
        }

        private static async Task CountdownTimer(int seconds)
        {
            for (int i = seconds; i > 0; i--)
            {
                hud.LastMessage = $"Starting in {i} seconds...";
                await Task.Delay(1000);
            }
        }

        private static void InitializeDiscordRPC()
        {
            discordClient = new DiscordRpcClient("112233445566778899");
            discordClient.Initialize();
            discordClient.SetPresence(new RichPresence
            {
                Details = "SpyNet",
                State = "In main menu",
                Timestamps = Timestamps.FromTimeSpan(2000),
                Assets = new Assets
                {
                    LargeImageKey = "logo",
                    LargeImageText = "Filaza Client"
                }
            });
        }

        private static Table RenderTable()
        {
            var table = new Table().NoBorder().Centered();
            table.AddColumn("[yellow]Metric[/]");
            table.AddColumn("[yellow]Value[/]");

            table.AddRow("Connection", hud.Connected ? "[green]Open[/]" : "[red]Closed[/]");
            table.AddRow("Ping", $"{hud.PingMs} ms");
            table.AddRow("Server", serverUrl);
            table.AddRow("Baseboard SN", baseboardSerial);
            table.AddRow("Last IP", hud.LastIP ?? "—");
            table.AddRow("PC Specs", hud.PCSpecs ?? "—");
            table.AddRow("Last Msg", hud.LastMessage ?? "—");
            if (!string.IsNullOrEmpty(hud.SiteUrl))
                table.AddRow("Site URL", $"[link={hud.SiteUrl}]{hud.SiteUrl}[/]");

            return table;
        }

        static async Task Connect()
        {
            try
            {
                ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(serverUrl), CancellationToken.None);
                connected = ws.State == WebSocketState.Open;
                hud.Connected = connected;
                hud.PingMs = GetPing("34.147.8.12");
            }
            catch (Exception ex)
            {
                hud.LastMessage = $"[red]Connection error: {ex.Message}[/]";
                connected = false;
            }
        }

        static async Task RecvLoop()
        {
            var buffer = new byte[4096];
            while (connected && ws.State == WebSocketState.Open)
            {
                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await Shutdown();
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    hud.LastMessage = message;
                    await HandleMsg(message);
                }
                catch
                {
                    await Shutdown();
                    break;
                }
            }
        }

        static async Task Shutdown()
        {
            connected = false;
            hud.Connected = false;
            if (ws != null && ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutdown", CancellationToken.None);

            discordClient?.Dispose();
            hud.SiteUrl = null;
        }

        static async Task HeartbeatLoop()
        {
            while (connected)
            {
                await Task.Delay(10000);
                hud.PingMs = GetPing("34.147.8.12");
                await SendAsync(JsonConvert.SerializeObject(new
                {
                    action = "HEARTBEAT",
                    baseboard_serial = baseboardSerial
                }));
            }
        }

        static async Task HandleMsg(string raw)
        {
            try
            {
                dynamic data = JsonConvert.DeserializeObject(raw);
                if (data.action == "REQUEST_PC_DATA") await SendPCData();
                else if (data.action == "CONNECTION_STATUS") HandleConnStatus(data);
                else if (data.action == "SITE_STATUS") HandleSiteStatus(data);
            }
            catch
            {
                hud.LastMessage = "[red]Invalid message format[/]";
            }
        }

        static async Task SendPCData()
        {
            hud.LastIP = GetIP();
            hud.PCSpecs = GetPCSpecs();
            await SendAsync(JsonConvert.SerializeObject(new
            {
                action = "SEND_PC_DATA",
                baseboard_serial = baseboardSerial,
                real_ip = hud.LastIP,
                pc_specs = hud.PCSpecs,
                old_enc_key = oldEncKey
            }));
        }

        static async Task HUDLoop()
        {
            while (connected)
            {
                Console.Clear();
                AnsiConsole.Write(new Panel(RenderTable()).Border(BoxBorder.Rounded).Header("Filaza HUD", Justify.Center).Padding(1, 1));
                await Task.Delay(5000);
            }
        }

        static void HandleConnStatus(dynamic data)
        {
            if (data.status == "SUCCESS" && data.new_enc_key != null)
            {
                oldEncKey = data.new_enc_key;
                SaveEncKey();
            }
            else if (data.status != "SUCCESS")
            {
                hud.LastMessage = $"[red]Connection rejected: {data.message}[/]";
                _ = Shutdown();
            }
        }

        static void HandleSiteStatus(dynamic data)
        {
            if (data.status == "SUCCESS")
            {
                hud.SiteUrl = data.url;
                OpenBrowser(hud.SiteUrl);
            }
        }

        static async Task RequestSite()
        {
            await SendAsync(JsonConvert.SerializeObject(new { action = "REQUEST_SITE" }));
        }

        static async Task SendAsync(string message)
        {
            if (ws?.State != WebSocketState.Open) return;

            var buffer = Encoding.UTF8.GetBytes(message);
            await ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        static string GetIP()
        {
            try
            {
                using var httpClient = new HttpClient();
                return httpClient.GetStringAsync("https://api.ipify.org").Result.Trim();
            }
            catch { return "N/A"; }
        }

        static string GetPCSpecs()
        {
            try
            {
                var sb = new StringBuilder();
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                    foreach (var obj in searcher.Get())
                        sb.Append($"CPU: {obj["Name"]}; ");

                using (var searcher = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory"))
                {
                    long total = 0;
                    foreach (ManagementObject obj in searcher.Get())
                        total += Convert.ToInt64(obj["Capacity"]);
                    sb.Append($"RAM: {total / (1024L * 1024 * 1024)}GB; ");
                }
                return sb.ToString();
            }
            catch { return "Unknown"; }
        }

        static string GetBaseboardSerial()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard");
                foreach (ManagementObject obj in searcher.Get())
                    return obj["SerialNumber"]?.ToString()?.Trim() ?? Guid.NewGuid().ToString();
            }
            catch { }

            return Guid.NewGuid().ToString();
        }

        static void OpenBrowser(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        static void LoadEncKey()
        {
            try { if (File.Exists(secretCfgPath)) oldEncKey = File.ReadAllText(secretCfgPath); }
            catch { }
        }

        static void SaveEncKey()
        {
            try { File.WriteAllText(secretCfgPath, oldEncKey); }
            catch { }
        }

        static long GetPing(string host)
        {
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(host, 1000);
                return reply?.Status == IPStatus.Success ? reply.RoundtripTime : -1;
            }
            catch { return -1; }
        }

        class HUDState
        {
            public bool Connected { get; set; }
            public string LastIP { get; set; }
            public string PCSpecs { get; set; }
            public string LastMessage { get; set; }
            public string SiteUrl { get; set; }
            public long PingMs { get; set; }
        }
    }
}

/*
  Code written by:
  Fizurea

  With support from:
  @ne_kenti, @dubiln (olar00nd), @kistolfshdwrz, Reif Maestro Team.

  Make changes at your own risk!
  Any attempt to inject malicious code will result in your request being blocked 
  and your account permanently banned.

  For all inquiries and collaboration:
  https://t.me/ne_kenti
*/