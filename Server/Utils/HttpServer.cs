using System.Net;
using Server.MirEnvir;
using S = ServerPackets;

namespace Server.Library.Utils
{
    class HttpServer : HttpService
    {
        Thread _thread;
        CancellationTokenSource tokenSource = new();

        public HttpServer()
        {
            Host = Settings.HTTPIPAddress;
        }

        public void Start()
        {
            _thread = new Thread(Listen);
            _thread.Start(tokenSource.Token);
        }

        public new void Stop()
        {
            base.Stop();
            
            tokenSource.Cancel();
            Thread.Sleep(1000);
            tokenSource.Dispose();

        }


        public override void OnGetRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            var url = request.Url.PathAndQuery;
            if (url.Contains("?"))
            {
                url = url.Substring(0, url.IndexOf("?", StringComparison.Ordinal));
                url = url.ToLower();
            }
            try
            {
                switch (url)
                {
                    case "/":
                        WriteResponse(response, GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.GameName));
                        break;
                    case "/newaccount":
                        var id = request.QueryString["id"];
                        var psd = request.QueryString["psd"];
                        var email = request.QueryString["email"];
                        var name = request.QueryString["name"];
                        var question = request.QueryString["question"];
                        var answer = request.QueryString["answer"];
                        var ip = request.QueryString["ip"];
                        var p = new ClientPackets.NewAccount();
                        p.AccountID = id;
                        p.Password = psd;
                        p.EMailAddress = email;
                        p.UserName = name;
                        p.SecretQuestion = question;
                        p.SecretAnswer = answer;
                        var result = Envir.Main.HTTPNewAccount(p, ip);
                        WriteResponse(response, result.ToString());
                        break;                               
                    case "/addnamelist":
                        id = request.QueryString["id"];
                        var fileName = request.QueryString["fileName"];
                        AddNameList(id, fileName);
                        WriteResponse(response, "true");
                        break;              
                    case "/broadcast":
                        var msg = request.QueryString["msg"];
                        if (msg.Length < 5)
                        {
                            WriteResponse(response, "short");
                            return;
                        }
                        Envir.Main.Broadcast(new S.Chat
                        {
                            Message = msg.Trim(),
                            Type = ChatType.Shout2
                        });
                        WriteResponse(response, "true");
                        break;
                    default:
                        WriteResponse(response, "error");
                        break;
                }
            }
            catch (Exception error)
            {
                // X1-20: log the detail, never echo it to the caller.
                MessageQueue.Instance.Enqueue("[HttpServer] request error: " + error.Message);
                WriteResponse(response, "request error");
            }
        }      

        void AddNameList(string playerName, string fileName)
        {
            // X1-20: confine the file to the name-list folder (Path.Combine returns a rooted fileName unchanged, and ".." was
            // not filtered) and keep the name to one line.
            if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(fileName) || playerName.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new ArgumentException("missing or invalid id/fileName");
            var root = Path.GetFullPath(Settings.NameListPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            fileName = Path.GetFullPath(Path.Combine(root, fileName));
            if (!fileName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("fileName outside the name-list folder");
            var sDirectory = Path.GetDirectoryName(fileName);
            Directory.CreateDirectory(sDirectory ?? throw new InvalidOperationException());
            if (!File.Exists(fileName))
                File.Create(fileName).Close();

            var tempString = fileName;
            if (File.ReadAllLines(tempString).All(t => playerName != t))
            {
                using (var line = File.AppendText(tempString))
                {
                    line.WriteLine(playerName);
                }
            }
        }    

        public override void OnPostRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            MessageQueue.Instance.EnqueueDebugging("[HttpServer] POST request: " + request.Url);
        }
    }

}
