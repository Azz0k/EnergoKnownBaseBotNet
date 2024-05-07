using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types.ReplyMarkups;
using TestWorkerService;
using static System.Formats.Asn1.AsnWriter;

namespace TestWorkerService
{
    public class BotService
    {
        public TableOFContents? _tableOfContents;
        private IServiceProvider _serviceProvider;
        private ILogger<WindowsBackgroundService> _logger;
        private HashSet<long>  _cachedAutorizedIds = new HashSet<long>();
        private AppSettings _settings;
        public bool isDataReceived = false;
        public BotService(IOptions<AppSettings> settings, ILogger<WindowsBackgroundService> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _settings = settings.Value;
            _serviceProvider = serviceProvider;
        }

        public bool IsIdAllowedDb(long id)
        {
            if (_cachedAutorizedIds.Contains(id))
            {
                return true;
            }
            using (var scope = _serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<DBRepository>();
                bool result = dbContext.Users.AsEnumerable().Where(x => x.ID == id).Any();
                if (result) 
                {
                    _cachedAutorizedIds.Add(id);
                    return true;
                }
            }
            return false;
        }
        public bool IsPhoneNumberAllowed(string phoneNumber, long id)
        {
            using(var scope = _serviceProvider.CreateScope()){
                var dbContext = scope.ServiceProvider.GetRequiredService<DBRepository>();
                bool result = dbContext.Users.AsEnumerable().Where(x => x.PhoneNumber.EndsWith(phoneNumber)).Any();
                if (result)
                {
                    dbContext.Users.Where(x => x.PhoneNumber.EndsWith(phoneNumber)).ExecuteUpdate(users => users.SetProperty(x=>x.ID,id));
                    //Users user = dbContext.Users.Where(x => x.PhoneNumber.EndsWith(phoneNumber)).ExecuteUpdate() ;
                    //if (user != null) {
                    //    user.ID = id;
                    //}
                    //dbContext.SaveChanges();
                    return true;
                }
            }
            return false;
        }
        public InlineKeyboardMarkup createStandardMarkup(string id = "")
        {
            string prefix = _settings.CallBackDataPrefix;
            if (id != "") 
            {
                id = id.Split(' ')[1];
            }
            else
            {
                id = _settings.HeadId;
            }
            
            List<List<InlineKeyboardButton>> buttons = new List<List<InlineKeyboardButton>>();
            var currentFolder = TableOFContents.GetElementById(id); 
            foreach (TableOFContents t in currentFolder.SubFolders.Values)
            {
                buttons.Add(new List<InlineKeyboardButton>(1) {
                    new InlineKeyboardButton(t.Name)
                    {
                        CallbackData = prefix + t.Id
                    }
                });
            }
            foreach (File f in currentFolder.Files.Values)
            {
                buttons.Add(new List<InlineKeyboardButton>(1) {
                    InlineKeyboardButton.WithUrl(f.Name, f.Url)
                });
            }
            var backSymbol = Char.ConvertFromUtf32(Convert.ToInt32(_settings.BackSymbol,16));
            var homeSymbol = Char.ConvertFromUtf32(Convert.ToInt32(_settings.HomeSymbol,16));

            string backId = TableOFContents.GetElementById(id).Parent?.Id ?? _settings.HeadId;

            buttons.Add(new List<InlineKeyboardButton>()
            {
                InlineKeyboardButton.WithCallbackData($"{backSymbol}",callbackData: prefix + backId),
                InlineKeyboardButton.WithCallbackData($"{homeSymbol}",callbackData: prefix + _settings.HeadId),
            }) ;
            return new InlineKeyboardMarkup(buttons);

        }
        async public Task<Boolean> IsIdAllowed(long id) 
        {
            if (_cachedAutorizedIds.Contains(id))
            {
                return true;
            }
            HttpClient httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri(_settings.CheckIdUrl);
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpClient.DefaultRequestHeaders.ConnectionClose = true;
            var response = await (await httpClient.GetAsync(id.ToString())).EnsureSuccessStatusCode().Content.ReadAsAsync<IsAutorized>();
            if (response.Authorization == "authorized")
            {
                _cachedAutorizedIds.Add(id);
                return true;
            }
            return false;
        }
        async public Task UpdateTableAsync()
        {
            HttpClient httpClient = new HttpClient();
            string url = _settings.BitrixApiUrl;
            Uri baseUri = new Uri(url);
            httpClient.BaseAddress = baseUri;
            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.ConnectionClose = true;
            string clientId = _settings.BitrixApiLogin;
            string clientSecret = _settings.BitrixApiPassword;
            var authenticationString = $"{clientId}:{clientSecret}";
            var base64EncodedAuthenticationString = Convert.ToBase64String(System.Text.ASCIIEncoding.ASCII.GetBytes(authenticationString));
            var requestMessage = new HttpRequestMessage(HttpMethod.Get, "");
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64EncodedAuthenticationString);
            string response = string.Empty;
            try
            {
                response = await (await httpClient.SendAsync(requestMessage)).EnsureSuccessStatusCode().Content.ReadAsStringAsync();
                isDataReceived = true;
                //string response = await(await httpClient.SendAsync(requestMessage)).EnsureSuccessStatusCode().Content.ReadAsStringAsync();
                //string filePath = @"c:\temp\jsonb24.txt";
                //string response = System.IO.File.ReadAllText(filePath);
                Disk disk = JsonConvert.DeserializeObject<Disk>(response);
                Root jsonRoot = JsonConvert.DeserializeObject<Root>(disk.Data);
                TableOFContents.ClearIndex();   
                _tableOfContents = new TableOFContents((JObject)jsonRoot.KnowledgeBase["2523"], "2523");
            }
            catch (Exception ex)
            {
                isDataReceived = false;
                _logger.LogError(ex, "{Message}", ex.Message);
            }
            
        }

    }
    public class TableOFContents 
    {
        public TableOFContents? Parent { get; set; }
        public string Name = string.Empty;
        public string Url = string.Empty; 
        public string Id = string.Empty;
        private static Dictionary<string, TableOFContents> _index = new();
        public SortedList<string,TableOFContents> SubFolders = new();
        public SortedList<string,File> Files = new();
        public static TableOFContents GetElementById(string id) => _index[id];
        public static void ClearIndex()
        {
            _index = new();
        }
        public TableOFContents(JObject source, string id, TableOFContents? parent = null )
        {
            Id = id;
            _index.Add(Id, this);
            Parent = parent;
            Name = ((string)source["name"]).Replace("_"," ");
            Url = (string)source["url"];
            foreach (var element in source["subfolders"])
            {
                foreach (var inner in element)
                {
                    if (inner is JArray)
                    {
                        foreach (var jsonFile in inner)
                        {
                            var objFile = jsonFile.ToObject<File>();
                            Files.Add(objFile.Name, objFile);
                        }

                    }
                    else 
                    {
                        var temp = new TableOFContents((JObject)inner, ((JProperty)element).Name, this);
                        SubFolders.Add(temp.Name, temp);
                    }
                    
                }

                
            }
                
            
        }

    }
    public class Disk
    {
        [JsonProperty("status")]
        public required string Status { get; set; }
        [JsonProperty("data")]
        public required string Data { get; set; }
        //public required Dictionary<string, JToken> Data { get; set; }
        [JsonProperty("errors")]
        public required List<object> Errors { get; set; }
    }

    public class Root
    {
        [JsonExtensionData]
        public required Dictionary<string, JToken> KnowledgeBase { get; set; }
    }

  
    public class File
    {
        [JsonProperty("id")]
        public required string Id { get; set; }
        [JsonProperty("name")]
        public required string Name { get; set; }
        [JsonProperty("url")]
        public required string Url { get; set; }
    }
    public class IsAutorized 
    {
        [JsonProperty("authorization")]
        public required string Authorization { get; set; }
    }
    
}