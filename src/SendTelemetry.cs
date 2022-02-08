using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Azure.Security.KeyVault.Secrets;
using Azure.Identity;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Azure.WebJobs.Extensions.Http;
using System.Net.Http;
using System.Linq;

namespace RaptBrewfather
{
    public class SendTelemetry
    {
        static readonly HttpClient httpClient = new HttpClient();

        [FunctionName("SendTelemetry")]
        public void Run([TimerTrigger("0 */20 * * * *")]TimerInfo myTimer, ILogger log)
        {
            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");
        }

        public static async Task<bool> CheckRaptBearerToken(ILogger log) {
            string keyVaultUri = System.Environment.GetEnvironmentVariable("KeyVaultUri", System.EnvironmentVariableTarget.Process);

            var secretClient = new SecretClient(new System.Uri(keyVaultUri), new DefaultAzureCredential());
            KeyVaultSecret accessToken = secretClient.GetSecret("accesstoken");
            if (accessToken.Properties.ExpiresOn < System.DateTimeOffset.UtcNow) {
                try {
                    // Create the body of the request
                    var data = new List<KeyValuePair<string, string>>();
                    data.Add(new KeyValuePair<string, string>("client_id", "rapt-user"));
                    data.Add(new KeyValuePair<string, string>("grant_type","password"));
                    data.Add(new KeyValuePair<string, string>("username",System.Environment.GetEnvironmentVariable("RaptUsername", System.EnvironmentVariableTarget.Process)));
                    data.Add(new KeyValuePair<string, string>("password",System.Environment.GetEnvironmentVariable("RaptApiKey", System.EnvironmentVariableTarget.Process)));

                    var req = new HttpRequestMessage(HttpMethod.Post, "https://id.rapt.io/connect/token"){
                        Content = new FormUrlEncodedContent(data)
                    };

                    var res = await httpClient.SendAsync(req);
                    string response = await res.Content.ReadAsStringAsync();

                    // Parse the JSON response
                    BearerTokenResponse bearerTokenResponse = JsonSerializer.Deserialize<BearerTokenResponse>(response);

                    // Create a new version of the secret
                    KeyVaultSecret updatedSecret = new KeyVaultSecret("accesstoken", bearerTokenResponse.access_token);
                    updatedSecret.Properties.ExpiresOn = System.DateTimeOffset.UtcNow.AddSeconds(bearerTokenResponse.expires_in);

                    // Commit the new version of the secret
                    KeyVaultSecret update = secretClient.SetSecret(updatedSecret);
                }
                catch {
                    // ok
                }
                return true;
            }
            else {
                return true;
            }
        }
        public static async Task<IEnumerable<string>> GetHydrometers(string bearerToken, ILogger log) {
            string requestUri = "https://api.rapt.io/api/Hydrometers/GetHydrometers";
            // Add bearer header
            var res = await httpClient.GetAsync(requestUri);
            string response = await res.Content.ReadAsStringAsync();
            
            var jsonResponse = JsonDocument.Parse(response);

            var hydrometers = jsonResponse.RootElement.EnumerateObject()
                                .Where(n => n.Name.Equals("id") && n.Value.ValueKind == JsonValueKind.String)
                                .Select(s => s.Value.ToString());

            return hydrometers;
        }

        public static async Task<BrewfatherStream> GetHydrometerTelemetry(string bearerToken, string hydrometerId, ILogger log) {
            // Get date data
            // Date should be in this format - 2021-12-20T07:32:46.467Z
            DateTime now = DateTime.UtcNow;
            DateTime previous = now.AddHours(-1);

            // Create the body of the request
            var data = new List<KeyValuePair<string, string>>();
            data.Add(new KeyValuePair<string, string>("hydrometerId", hydrometerId));
            data.Add(new KeyValuePair<string, string>("startDate", previous.ToString("s")));
            data.Add(new KeyValuePair<string, string>("endDate", now.ToString("s")));

            var req = new HttpRequestMessage(HttpMethod.Post, "https://id.rapt.io/connect/token"){
                Content = new FormUrlEncodedContent(data)
            };

            var res = await httpClient.SendAsync(req);
            string response = await res.Content.ReadAsStringAsync();

            var test = JsonSerializer.Deserialize<List<RaptTelemetry>>(response);

            var recentResult = test.OrderByDescending(t => t.createdOn).First();

            var brewfather = new BrewfatherStream(){
                name = hydrometerId,
                temp = recentResult.temperature,
                temp_unit = "C",
                gravity = Math.Round(recentResult.gravity / 1000, 3),
                gravity_unit = "G"
            };

            return brewfather;
        }
    }

    public class BearerTokenResponse
    {
        public string access_token { get; set; }
        public int expires_in { get; set; }
        public string token_type { get; set; }
        public string scope { get; set; }
    }

    public class BrewfatherStream
    {
        public string name { get; set; }
        public double temp { get; set; }
        public string temp_unit { get; set; }
        public double gravity { get; set; }
        public string gravity_unit { get; set; }
    }

    public class RaptTelemetry
    {
        public string id { get; set; }
        public DateTime createdOn { get; set; }
        public double temperature { get; set; }
        public double gravity { get; set; }
        public int rssi { get; set; }
        public double battery { get; set; }
        public string version { get; set; }
    }
}
